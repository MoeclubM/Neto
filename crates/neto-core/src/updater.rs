use crate::doh::SHARED_HTTP_CLIENT;
use anyhow::{Context, Result, anyhow, bail, ensure};
use reqwest::header::{ACCEPT, HeaderMap, HeaderValue, USER_AGENT};
use serde::{Deserialize, Serialize};
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::AsyncWriteExt;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct UpdateCheckResult {
    pub current_version: String,
    pub latest_version: String,
    pub has_update: bool,
    pub release_url: String,
    pub release_notes: String,
    pub published_at: String,
    pub download_url: String,
    pub download_name: String,
    pub installer_url: String,
    pub installer_name: String,
    pub archive_url: String,
    pub archive_name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DownloadUpdateResult {
    pub path: String,
    pub bytes: u64,
}

#[derive(Debug, Clone, Deserialize)]
struct GithubRelease {
    #[serde(default)]
    tag_name: String,
    #[serde(default)]
    html_url: String,
    #[serde(default)]
    body: Option<String>,
    #[serde(default)]
    published_at: Option<String>,
    #[serde(default)]
    assets: Vec<GithubAsset>,
}

#[derive(Debug, Clone, Deserialize)]
struct GithubAsset {
    #[serde(default)]
    name: String,
    #[serde(default)]
    browser_download_url: String,
}

pub fn normalize_repo_slug(repo: &str) -> Result<String> {
    let trimmed = repo.trim().trim_end_matches('/').trim_end_matches(".git");
    ensure!(!trimmed.is_empty(), "GitHub 仓库地址为空");
    if let Ok(url) = reqwest::Url::parse(trimmed) {
        ensure!(
            url.host_str()
                .is_some_and(|h| h.eq_ignore_ascii_case("github.com")),
            "不支持的仓库域名"
        );
        let segs: Vec<&str> = url
            .path_segments()
            .map(|s| s.filter(|p| !p.is_empty()).collect())
            .unwrap_or_default();
        ensure!(segs.len() >= 2, "GitHub 仓库路径无效");
        return Ok(format!("{}/{}", segs[0], segs[1]));
    }
    let parts: Vec<&str> = trimmed.split('/').filter(|p| !p.is_empty()).collect();
    ensure!(parts.len() == 2, "GitHub 仓库标识格式无效");
    Ok(format!("{}/{}", parts[0], parts[1]))
}

pub fn parse_semver_key(raw: &str) -> ((u64, u64, u64), bool) {
    let cleaned = raw
        .trim()
        .trim_start_matches(['v', 'V'])
        .trim_end_matches(".debug");
    let without_build = cleaned.split('+').next().unwrap_or(cleaned);
    let (core, is_prerelease) = match without_build.split_once('-') {
        Some((c, pre)) => (c, !pre.trim().is_empty()),
        None => (without_build, false),
    };
    let mut nums = [0u64; 3];
    for (idx, seg) in core.split('.').take(3).enumerate() {
        let digits: String = seg.chars().take_while(|c| c.is_ascii_digit()).collect();
        nums[idx] = digits.parse::<u64>().unwrap_or(0);
    }
    ((nums[0], nums[1], nums[2]), is_prerelease)
}

pub fn is_newer_version(current: &str, latest: &str) -> bool {
    let (cur_ver, cur_pre) = parse_semver_key(current);
    let (lat_ver, lat_pre) = parse_semver_key(latest);
    if lat_ver == (0, 0, 0) {
        return false;
    }
    lat_ver > cur_ver || (lat_ver == cur_ver && cur_pre && !lat_pre)
}

fn select_platform_assets(
    assets: &[GithubAsset],
    platform: &str,
    arch: &str,
    prefer_installer: bool,
) -> (String, String, String, String, String, String) {
    let plat = platform.trim().to_ascii_lowercase();
    let arch_lc = arch.trim().to_ascii_lowercase();
    let is_arm64 = arch_lc == "aarch64" || arch_lc == "arm64";
    let is_arm32 = arch_lc == "arm" || arch_lc == "armv7" || arch_lc == "armeabi-v7a";

    match plat.as_str() {
        "windows" => {
            let mut installer = (String::new(), String::new());
            let mut archive = (String::new(), String::new());
            for a in assets {
                let name_lc = a.name.to_ascii_lowercase();
                if a.browser_download_url.is_empty() {
                    continue;
                }
                let asset_is_arm = name_lc.contains("arm64") || name_lc.contains("aarch64");
                if is_arm64 != asset_is_arm {
                    continue;
                }
                if installer.0.is_empty() && name_lc.ends_with(".exe") && name_lc.contains("setup")
                {
                    installer = (a.browser_download_url.clone(), a.name.clone());
                } else if archive.0.is_empty()
                    && name_lc.ends_with(".zip")
                    && name_lc.contains("win")
                {
                    archive = (a.browser_download_url.clone(), a.name.clone());
                }
            }
            let primary = if prefer_installer && !installer.0.is_empty() {
                installer.clone()
            } else if !archive.0.is_empty() {
                archive.clone()
            } else {
                installer.clone()
            };
            (
                primary.0,
                primary.1,
                installer.0,
                installer.1,
                archive.0,
                archive.1,
            )
        }
        "linux" => {
            let mut matched = (String::new(), String::new());
            for a in assets {
                let name_lc = a.name.to_ascii_lowercase();
                if a.browser_download_url.is_empty() || !name_lc.ends_with(".deb") {
                    continue;
                }
                let asset_is_arm = name_lc.contains("arm64") || name_lc.contains("aarch64");
                if is_arm64 == asset_is_arm {
                    matched = (a.browser_download_url.clone(), a.name.clone());
                    break;
                }
            }
            (
                matched.0.clone(),
                matched.1.clone(),
                matched.0,
                matched.1,
                String::new(),
                String::new(),
            )
        }
        "android" => {
            let mut universal = (String::new(), String::new());
            let mut abi_match = (String::new(), String::new());
            let mut fallback = (String::new(), String::new());
            for a in assets {
                let name_lc = a.name.to_ascii_lowercase();
                if a.browser_download_url.is_empty() || !name_lc.ends_with(".apk") {
                    continue;
                }
                if fallback.0.is_empty() {
                    fallback = (a.browser_download_url.clone(), a.name.clone());
                }
                if universal.0.is_empty() && name_lc.contains("universal") {
                    universal = (a.browser_download_url.clone(), a.name.clone());
                }
                let matches_abi = if is_arm64 {
                    name_lc.contains("arm64-v8a") || name_lc.contains("arm64")
                } else if is_arm32 {
                    name_lc.contains("armeabi-v7a")
                } else {
                    name_lc.contains("x86_64")
                };
                if abi_match.0.is_empty() && matches_abi {
                    abi_match = (a.browser_download_url.clone(), a.name.clone());
                }
            }
            let chosen = if !abi_match.0.is_empty() {
                abi_match
            } else if !universal.0.is_empty() {
                universal
            } else {
                fallback
            };
            (
                chosen.0.clone(),
                chosen.1.clone(),
                chosen.0,
                chosen.1,
                String::new(),
                String::new(),
            )
        }
        _ => (
            String::new(),
            String::new(),
            String::new(),
            String::new(),
            String::new(),
            String::new(),
        ),
    }
}

fn build_custom_client(
    proxy_port: Option<u16>,
    connect_timeout_secs: u64,
    total_timeout_secs: u64,
) -> Result<reqwest::Client> {
    let mut roots = rustls::RootCertStore::empty();
    roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    let tls = rustls::ClientConfig::builder_with_provider(Arc::new(
        rustls::crypto::ring::default_provider(),
    ))
    .with_safe_default_protocol_versions()
    .context("configure rustls protocol versions")?
    .with_root_certificates(roots)
    .with_no_client_auth();

    let mut builder = reqwest::Client::builder()
        .use_preconfigured_tls(tls)
        .connect_timeout(Duration::from_secs(connect_timeout_secs))
        .timeout(Duration::from_secs(total_timeout_secs));
    if let Some(port) = proxy_port {
        builder = builder.proxy(reqwest::Proxy::all(format!("http://127.0.0.1:{port}"))?);
    } else {
        builder = builder.no_proxy();
    }
    builder.build().context("build updater HTTP client")
}

fn build_headers(user_agent: &str, accept: &str) -> Result<HeaderMap> {
    let mut headers = HeaderMap::new();
    let ua = if user_agent.trim().is_empty() {
        "Neto"
    } else {
        user_agent.trim()
    };
    headers.insert(USER_AGENT, HeaderValue::from_str(ua)?);
    headers.insert(ACCEPT, HeaderValue::from_str(accept)?);
    Ok(headers)
}

async fn get_with_fallback(
    url: &str,
    local_proxy_port: Option<u16>,
    user_agent: &str,
    accept: &str,
    connect_timeout_secs: u64,
    total_timeout_secs: u64,
) -> Result<reqwest::Response> {
    let headers = build_headers(user_agent, accept)?;
    let mut last_err: Option<anyhow::Error> = None;

    if let Some(port) = local_proxy_port {
        for mode in [Some(port), None] {
            let client = match build_custom_client(mode, connect_timeout_secs, total_timeout_secs) {
                Ok(c) => c,
                Err(e) => {
                    last_err = Some(e);
                    continue;
                }
            };
            match client.get(url).headers(headers.clone()).send().await {
                Ok(resp) if resp.status().is_success() => return Ok(resp),
                Ok(resp) => {
                    last_err = Some(anyhow!("HTTP {}", resp.status().as_u16()));
                }
                Err(e) => {
                    last_err = Some(e.into());
                }
            }
        }
    } else {
        match SHARED_HTTP_CLIENT
            .get(url)
            .headers(headers.clone())
            .timeout(Duration::from_secs(total_timeout_secs))
            .send()
            .await
        {
            Ok(resp) if resp.status().is_success() => return Ok(resp),
            Ok(resp) => {
                last_err = Some(anyhow!("HTTP {}", resp.status().as_u16()));
            }
            Err(e) => {
                last_err = Some(e.into());
            }
        }
        if let Ok(direct_client) =
            build_custom_client(None, connect_timeout_secs, total_timeout_secs)
        {
            match direct_client.get(url).headers(headers).send().await {
                Ok(resp) if resp.status().is_success() => return Ok(resp),
                Ok(resp) => {
                    last_err = Some(anyhow!("HTTP {}", resp.status().as_u16()));
                }
                Err(e) => {
                    last_err = Some(e.into());
                }
            }
        }
    }

    Err(last_err.unwrap_or_else(|| anyhow!("请求更新接口失败")))
}

pub async fn check_github_release(
    repo: &str,
    current_version: &str,
    platform: Option<&str>,
    arch: Option<&str>,
    prefer_installer: bool,
    local_proxy_port: Option<u16>,
    user_agent: &str,
) -> Result<UpdateCheckResult> {
    let slug = normalize_repo_slug(repo)?;
    let api_url = format!("https://api.github.com/repos/{slug}/releases/latest");
    let resp = get_with_fallback(
        &api_url,
        local_proxy_port,
        user_agent,
        "application/vnd.github+json",
        10,
        25,
    )
    .await
    .with_context(|| format!("检查更新失败 ({slug})"))?;

    let release: GithubRelease = resp.json().await.context("解析 GitHub Release 响应失败")?;
    let latest_version = release.tag_name.trim().to_string();
    if latest_version.is_empty() {
        bail!("GitHub Release 缺少版本号");
    }

    let plat = platform
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(std::env::consts::OS);
    let target_arch = arch
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(std::env::consts::ARCH);

    let has_update = is_newer_version(current_version, &latest_version);
    let (download_url, download_name, installer_url, installer_name, archive_url, archive_name) =
        select_platform_assets(&release.assets, plat, target_arch, prefer_installer);
    let release_url = if release.html_url.trim().is_empty() {
        format!("https://github.com/{slug}/releases/latest")
    } else {
        release.html_url
    };

    Ok(UpdateCheckResult {
        current_version: current_version.trim().to_string(),
        latest_version,
        has_update,
        release_url,
        release_notes: release.body.unwrap_or_default().trim().to_string(),
        published_at: release.published_at.unwrap_or_default(),
        download_url,
        download_name,
        installer_url,
        installer_name,
        archive_url,
        archive_name,
    })
}

pub async fn download_release_asset(
    url: &str,
    dest_path: &str,
    local_proxy_port: Option<u16>,
    user_agent: &str,
) -> Result<DownloadUpdateResult> {
    let trimmed_url = url.trim();
    let trimmed_dest = dest_path.trim();
    ensure!(!trimmed_url.is_empty(), "下载地址为空");
    ensure!(!trimmed_dest.is_empty(), "保存路径为空");

    let dest = Path::new(trimmed_dest);
    if let Some(parent) = dest.parent()
        && !parent.as_os_str().is_empty()
    {
        tokio::fs::create_dir_all(parent)
            .await
            .context("创建更新缓存目录失败")?;
    }

    let part_path = format!("{trimmed_dest}.part");
    let mut resp = get_with_fallback(
        trimmed_url,
        local_proxy_port,
        user_agent,
        "application/octet-stream",
        15,
        300,
    )
    .await
    .context("下载更新安装包失败")?;

    let mut file = tokio::fs::File::create(&part_path)
        .await
        .context("创建临时安装包文件失败")?;
    let mut total_bytes = 0u64;
    while let Some(chunk) = resp.chunk().await.context("读取更新安装包数据失败")? {
        file.write_all(&chunk)
            .await
            .context("写入更新安装包失败")?;
        total_bytes = total_bytes.saturating_add(chunk.len() as u64);
    }
    file.flush().await?;
    drop(file);

    if total_bytes == 0 {
        let _ = tokio::fs::remove_file(&part_path).await;
        bail!("下载的安装包为空");
    }
    if tokio::fs::try_exists(dest).await.unwrap_or(false) {
        let _ = tokio::fs::remove_file(dest).await;
    }
    tokio::fs::rename(&part_path, dest)
        .await
        .context("保存更新安装包失败")?;

    Ok(DownloadUpdateResult {
        path: trimmed_dest.to_string(),
        bytes: total_bytes,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_normalize_repo_slug() {
        assert_eq!(normalize_repo_slug("MoeclubM/Neto").unwrap(), "MoeclubM/Neto");
        assert_eq!(
            normalize_repo_slug("https://github.com/MoeclubM/XBClient.git").unwrap(),
            "MoeclubM/XBClient"
        );
        assert_eq!(
            normalize_repo_slug("https://github.com/MoeclubM/XBClient/releases").unwrap(),
            "MoeclubM/XBClient"
        );
    }

    #[test]
    fn test_semver_comparison() {
        assert!(is_newer_version("0.0.5", "v0.0.6"));
        assert!(is_newer_version("v0.0.5", "0.0.6"));
        assert!(is_newer_version("0.0.6-beta.2.abcd1234", "v0.0.6"));
        assert!(!is_newer_version("0.0.6", "v0.0.6"));
        assert!(!is_newer_version("0.0.7", "v0.0.6"));
        assert!(!is_newer_version("0.1.0", "v0.0.14"));
    }

    #[test]
    fn test_select_windows_and_linux_assets() {
        let assets = vec![
            GithubAsset {
                name: "Neto-v0.0.6-android-arm64-v8a.apk".into(),
                browser_download_url: "https://example.com/app.apk".into(),
            },
            GithubAsset {
                name: "Neto-v0.0.6-windows-x64-setup.exe".into(),
                browser_download_url: "https://example.com/setup.exe".into(),
            },
            GithubAsset {
                name: "Neto-v0.0.6-windows-x64.zip".into(),
                browser_download_url: "https://example.com/win.zip".into(),
            },
            GithubAsset {
                name: "neto_0.0.6_amd64.deb".into(),
                browser_download_url: "https://example.com/amd64.deb".into(),
            },
        ];
        let (dl, _, inst, _, arch_url, _) =
            select_platform_assets(&assets, "windows", "x86_64", true);
        assert_eq!(dl, "https://example.com/setup.exe");
        assert_eq!(inst, "https://example.com/setup.exe");
        assert_eq!(arch_url, "https://example.com/win.zip");

        let (dl_port, _, _, _, _, _) =
            select_platform_assets(&assets, "windows", "x86_64", false);
        assert_eq!(dl_port, "https://example.com/win.zip");

        let (deb, _, _, _, _, _) = select_platform_assets(&assets, "linux", "x86_64", true);
        assert_eq!(deb, "https://example.com/amd64.deb");
    }
}
