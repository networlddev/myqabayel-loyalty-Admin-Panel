(() => {
  const chart = document.getElementById("customerChart");
  const monthData = document.getElementById("customerMonthData");
  const yearData = document.getElementById("customerYearData");
  const periodSwitch = document.querySelector(".customer-period-switch");
  const typeSwitch = document.querySelector(".customer-chart-switcher");
  const total = document.getElementById("customerPerformanceTotal");
  const periodLabel = document.getElementById("customerPerformancePeriod");
  const change = document.getElementById("customerPerformanceChange");
  const description = document.getElementById("customerChartDescription");
  const note = document.getElementById("customerChartNote");
  if (!chart || !monthData || !yearData || !periodSwitch || !typeSwitch || !total || !periodLabel || !change || !description || !note) return;

  const svgNamespace = "http://www.w3.org/2000/svg";
  const top = 20;
  const bottom = 210;
  const left = 66;
  const right = 672;
  const validTypes = ["line", "bar", "area"];
  const validPeriods = ["month", "year"];
  let selectedType = "line";
  let selectedPeriod = "month";

  try {
    const savedType = localStorage.getItem("qabayel-customer-chart-view");
    const savedPeriod = localStorage.getItem("qabayel-customer-chart-period");
    if (validTypes.includes(savedType)) selectedType = savedType;
    if (validPeriods.includes(savedPeriod)) selectedPeriod = savedPeriod;
  } catch { /* The controls still work when storage is disabled. */ }

  function element(parent, name, attributes = {}, content) {
    const node = document.createElementNS(svgNamespace, name);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
    if (content !== undefined) node.textContent = content;
    parent.appendChild(node);
    return node;
  }

  function render() {
    const source = selectedPeriod === "month" ? monthData : yearData;
    const points = [...source.querySelectorAll("[data-count]")].map((point) => ({
      label: point.dataset.label || "",
      count: Math.max(0, Number(point.dataset.count) || 0)
    }));
    if (!points.length) return;

    const maxValue = Math.max(4, Math.ceil(Math.max(...points.map((point) => point.count)) / 4) * 4);
    points.forEach((point, index) => {
      point.x = left + (right - left) * index / Math.max(1, points.length - 1);
      point.y = bottom - point.count * (bottom - top) / maxValue;
    });

    const svg = document.createElementNS(svgNamespace, "svg");
    svg.setAttribute("viewBox", "0 0 720 260");
    svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
    svg.setAttribute("aria-hidden", "true");
    element(svg, "title", {}, `Customer registrations ${selectedType} graph`);

    if (selectedType === "area") {
      const definitions = element(svg, "defs");
      const gradient = element(definitions, "linearGradient", { id: "customerAreaGradient", x1: "0", y1: "0", x2: "0", y2: "1" });
      element(gradient, "stop", { offset: "0%", "stop-color": "#a7192f", "stop-opacity": ".32" });
      element(gradient, "stop", { offset: "100%", "stop-color": "#a7192f", "stop-opacity": ".01" });
    }

    for (let step = 0; step <= 4; step++) {
      const y = top + (bottom - top) * step / 4;
      element(svg, "line", { class: "chart-grid-line", x1: 46, y1: y, x2: 692, y2: y });
      element(svg, "text", { class: "chart-axis-label", x: 33, y: y + 4, "text-anchor": "end" }, String(maxValue * (4 - step) / 4));
    }

    if (selectedType === "bar") {
      const width = Math.min(48, (right - left) / (points.length + 1) * .7);
      points.forEach((point) => {
        const bar = element(svg, "rect", {
          class: "chart-svg-bar",
          x: point.x - width / 2,
          y: Math.min(point.y, bottom - 3),
          width,
          height: Math.max(3, bottom - point.y),
          rx: 7
        });
        element(bar, "title", {}, `${point.label}: ${point.count} new members`);
        element(svg, "text", { class: "chart-bar-value", x: point.x, y: Math.max(top + 10, point.y - 11), "text-anchor": "middle" }, String(point.count));
      });
    } else {
      const path = points.reduce((result, point, index) => {
        if (index === 0) return `M ${point.x} ${point.y}`;
        const previous = points[index - 1];
        const middle = (previous.x + point.x) / 2;
        return `${result} C ${middle} ${previous.y}, ${middle} ${point.y}, ${point.x} ${point.y}`;
      }, "");
      if (selectedType === "area") {
        element(svg, "path", { class: "chart-area-fill", d: `${path} L ${points[points.length - 1].x} ${bottom} L ${points[0].x} ${bottom} Z`, fill: "url(#customerAreaGradient)" });
      }
      element(svg, "path", { class: "chart-line-stroke", d: path });
      points.forEach((point) => {
        const dot = element(svg, "circle", { class: "chart-point", cx: point.x, cy: point.y, r: 5 });
        element(dot, "title", {}, `${point.label}: ${point.count} new members`);
      });
    }

    points.forEach((point) => {
      element(svg, "text", { class: "chart-axis-label chart-day-label", x: point.x, y: 248, "text-anchor": "middle" }, point.label);
    });

    chart.replaceChildren(svg);
    chart.setAttribute("aria-label", `New customer registrations ${selectedType} graph for the last ${selectedPeriod === "month" ? "30 days" : "12 months"}`);
    description.textContent = points.map((point) => `${point.label}: ${point.count} new members`).join(", ");
    total.textContent = (Number(source.dataset.total) || 0).toLocaleString();
    periodLabel.textContent = selectedPeriod === "month" ? "Last 30 days" : "Last 12 months";
    note.textContent = Number(source.dataset.total) === 0
      ? "No new registrations in this period."
      : "Counts show new registrations in each interval.";
    const changeValue = Number((source.dataset.change || "").replace(",", "."));
    if (source.dataset.change === "") {
      change.textContent = "New registrations; no previous-period baseline";
      change.classList.remove("negative");
      change.classList.add("no-baseline");
    } else {
      change.textContent = `${changeValue === 0 ? "No change" : `${changeValue < 0 ? "↘" : "↗"} ${Math.abs(changeValue)}%`} vs previous ${selectedPeriod === "month" ? "30 days" : "12 months"}`;
      change.classList.toggle("negative", changeValue < 0);
      change.classList.toggle("no-baseline", changeValue === 0);
    }
    periodSwitch.querySelectorAll("button[data-customer-period]").forEach((button) => {
      button.setAttribute("aria-pressed", String(button.dataset.customerPeriod === selectedPeriod));
    });
    typeSwitch.querySelectorAll("button[data-chart-type]").forEach((button) => {
      button.setAttribute("aria-pressed", String(button.dataset.chartType === selectedType));
    });
  }

  periodSwitch.addEventListener("click", (event) => {
    const button = event.target.closest("button[data-customer-period]");
    if (!button || !periodSwitch.contains(button)) return;
    selectedPeriod = button.dataset.customerPeriod;
    render();
    try { localStorage.setItem("qabayel-customer-chart-period", selectedPeriod); } catch { /* The view still switches. */ }
  });
  typeSwitch.addEventListener("click", (event) => {
    const button = event.target.closest("button[data-chart-type]");
    if (!button || !typeSwitch.contains(button)) return;
    selectedType = button.dataset.chartType;
    render();
    try { localStorage.setItem("qabayel-customer-chart-view", selectedType); } catch { /* The view still switches. */ }
  });

  render();
  chart.hidden = false;
  monthData.hidden = true;
  periodSwitch.hidden = false;
  typeSwitch.hidden = false;
})();
