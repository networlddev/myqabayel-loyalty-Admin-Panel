(() => {
  const chart = document.getElementById("campaignChart");
  const fallback = document.getElementById("campaignChartFallback");
  const switcher = document.querySelector(".chart-switcher");
  if (!chart || !fallback || !switcher) return;

  const days = [...fallback.querySelectorAll("[data-count]")].map((day) => ({
    label: day.dataset.label || "",
    count: Math.max(0, Number(day.dataset.count) || 0)
  }));
  if (!days.length) return;

  const svgNamespace = "http://www.w3.org/2000/svg";
  const left = 66;
  const right = 672;
  const top = 20;
  const bottom = 210;
  const plotHeight = bottom - top;
  const maxValue = Math.max(4, Math.ceil(Math.max(...days.map((day) => day.count)) / 4) * 4);
  const points = days.map((day, index) => ({
    ...day,
    x: left + (right - left) * index / Math.max(1, days.length - 1),
    y: bottom - day.count * plotHeight / maxValue
  }));

  function element(parent, name, attributes = {}, content) {
    const node = document.createElementNS(svgNamespace, name);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
    if (content !== undefined) node.textContent = content;
    parent.appendChild(node);
    return node;
  }

  function linePath() {
    return points.reduce((path, point, index) => {
      if (index === 0) return `M ${point.x} ${point.y}`;
      const previous = points[index - 1];
      const middle = (previous.x + point.x) / 2;
      return `${path} C ${middle} ${previous.y}, ${middle} ${point.y}, ${point.x} ${point.y}`;
    }, "");
  }

  function render(type) {
    const svg = document.createElementNS(svgNamespace, "svg");
    svg.setAttribute("viewBox", "0 0 720 260");
    svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
    svg.setAttribute("aria-hidden", "true");
    element(svg, "title", {}, `Campaign activity ${type} graph`);

    if (type === "area") {
      const definitions = element(svg, "defs");
      const gradient = element(definitions, "linearGradient", { id: "campaignAreaGradient", x1: "0", y1: "0", x2: "0", y2: "1" });
      element(gradient, "stop", { offset: "0%", "stop-color": "#a7192f", "stop-opacity": ".32" });
      element(gradient, "stop", { offset: "100%", "stop-color": "#a7192f", "stop-opacity": ".01" });
    }

    for (let step = 0; step <= 4; step++) {
      const y = top + plotHeight * step / 4;
      element(svg, "line", { class: "chart-grid-line", x1: 46, y1: y, x2: 692, y2: y });
      element(svg, "text", { class: "chart-axis-label", x: 33, y: y + 4, "text-anchor": "end" }, String(maxValue * (4 - step) / 4));
    }

    if (type === "bar") {
      points.forEach((point) => {
        const bar = element(svg, "rect", {
          class: "chart-svg-bar",
          x: point.x - 22,
          y: Math.min(point.y, bottom - 3),
          width: 44,
          height: Math.max(3, bottom - point.y),
          rx: 8
        });
        element(bar, "title", {}, `${point.label}: ${point.count} campaigns`);
        element(svg, "text", { class: "chart-bar-value", x: point.x, y: Math.max(top + 10, point.y - 11), "text-anchor": "middle" }, String(point.count));
      });
    } else {
      const path = linePath();
      if (type === "area") {
        element(svg, "path", { class: "chart-area-fill", d: `${path} L ${points.at(-1).x} ${bottom} L ${points[0].x} ${bottom} Z` });
      }
      element(svg, "path", { class: "chart-line-stroke", d: path });
      points.forEach((point) => {
        const dot = element(svg, "circle", { class: "chart-point", cx: point.x, cy: point.y, r: 5 });
        element(dot, "title", {}, `${point.label}: ${point.count} campaigns`);
      });
    }

    points.forEach((point) => {
      element(svg, "text", { class: "chart-axis-label chart-day-label", x: point.x, y: 248, "text-anchor": "middle" }, point.label);
    });

    chart.replaceChildren(svg);
    chart.setAttribute("aria-label", `Campaign activity ${type} graph for the last seven days`);
    switcher.querySelectorAll("button[data-chart-type]").forEach((button) => {
      button.setAttribute("aria-pressed", String(button.dataset.chartType === type));
    });
  }

  let selected = "line";
  try {
    const saved = localStorage.getItem("qabayel-campaign-chart-view");
    if (["line", "bar", "area"].includes(saved)) selected = saved;
  } catch { /* Browsers can disable local storage. */ }

  switcher.addEventListener("click", (event) => {
    const button = event.target.closest("button[data-chart-type]");
    if (!button || !switcher.contains(button)) return;
    selected = button.dataset.chartType;
    render(selected);
    try { localStorage.setItem("qabayel-campaign-chart-view", selected); } catch { /* The view still switches. */ }
  });

  render(selected);
  chart.hidden = false;
  fallback.hidden = true;
  switcher.hidden = false;
})();
