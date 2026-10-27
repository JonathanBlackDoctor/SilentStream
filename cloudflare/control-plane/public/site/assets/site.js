const SITE_CONFIG = {
  brandName: "SilentStream",
  version: "최신 릴리스",
  repoUrl: "https://github.com/JonathanBlackDoctor/SilentStream/tree/claude/relaxed-davinci-qmmjwl",
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
