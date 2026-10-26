/** Shared SVG sprite references (#icon-* symbols in index.html). */
function portalCreateIcon(name) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.classList.add("icon");
  svg.setAttribute("aria-hidden", "true");
  const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
  use.setAttribute("href", `#icon-${name}`);
  svg.appendChild(use);
  return svg;
}

function portalSetIconButton(button, iconName, visibleLabel) {
  if (!button) return;
  button.replaceChildren();
  button.appendChild(portalCreateIcon(iconName));
  if (visibleLabel) {
    const span = document.createElement("span");
    span.className = "btn-icon-label";
    span.textContent = visibleLabel;
    button.appendChild(span);
  }
}
