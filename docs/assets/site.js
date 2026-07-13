const SITE_CONFIG = {
  brandName: "Media Capture Helper",
  version: "0.3.0",
  repoUrl: "https://github.com/JonathanBlackDoctor/SilentStream",
  releasesPageUrl: "https://github.com/JonathanBlackDoctor/SilentStream/releases",
  downloadSetupUrl: "https://github.com/JonathanBlackDoctor/SilentStream/releases/latest/download/MediaCaptureHelper-win-Setup.exe",
};

document.querySelectorAll("[data-site-text]").forEach((element) => {
  const value = SITE_CONFIG[element.dataset.siteText];
  if (value != null) element.textContent = value;
});

document.querySelectorAll("[data-site-href]").forEach((element) => {
  const value = SITE_CONFIG[element.dataset.siteHref];
  if (value != null) element.href = value;
});

document.querySelectorAll("[data-current-year]").forEach((element) => {
  element.textContent = new Date().getFullYear();
});
