const portal = document.querySelector(".portal");
const toggle = document.querySelector(".motion-toggle");
const canAnimate = matchMedia("(prefers-reduced-motion: no-preference)");
function updateMotionControl() {
  toggle.hidden = !canAnimate.matches;
}
canAnimate.addEventListener("change", updateMotionControl);
updateMotionControl();
toggle.addEventListener("click", () => {
  const paused = portal.classList.toggle("motion-paused");
  toggle.setAttribute("aria-pressed", String(paused));
  toggle.textContent = paused ? "Resume animation" : "Pause animation";
});
