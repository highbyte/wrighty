export function relativeTimeLabel(value, now = Date.now()) {
  const instant = typeof value === "number" ? value : Date.parse(value);
  if (!Number.isFinite(instant)) return null;
  const seconds = Math.round((instant - now) / 1000);
  const future = seconds > 0;
  const absolute = Math.abs(seconds);
  if (absolute < 45) return "just now";
  const [amount, unit] = absolute < 3600
    ? [Math.round(absolute / 60), "m"]
    : absolute < 86400
      ? [Math.round(absolute / 3600), "h"]
      : absolute < 2592000
        ? [Math.round(absolute / 86400), "d"]
        : absolute < 31536000
          ? [Math.round(absolute / 2592000), "mo"]
          : [Math.round(absolute / 31536000), "y"];
  return future ? `in ${amount}${unit}` : `${amount}${unit} ago`;
}

export function localizeRelativeTimes(root, now = Date.now()) {
  root.querySelectorAll?.("time[data-relative-time]").forEach(element => {
    const relative = relativeTimeLabel(element.dateTime || element.getAttribute("datetime"), now);
    if (relative) element.textContent = `${element.dataset.timeLabel || "Updated"} ${relative}`;
  });
}
