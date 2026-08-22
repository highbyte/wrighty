export function relativeTimeLabel(value, now = Date.now()) {
  const instant = typeof value === "number" ? value : Date.parse(value);
  if (!Number.isFinite(instant)) return null;
  const seconds = Math.round((instant - now) / 1000);
  const future = seconds > 0;
  const absolute = Math.abs(seconds);
  if (absolute < 45) return "just now";
  let amount;
  let unit;
  if (absolute < 3600) {
    amount = Math.round(absolute / 60);
    unit = "m";
  } else if (absolute < 86400) {
    amount = Math.round(absolute / 3600);
    unit = "h";
  } else if (absolute < 2592000) {
    amount = Math.round(absolute / 86400);
    unit = "d";
  } else if (absolute < 31536000) {
    amount = Math.round(absolute / 2592000);
    unit = "mo";
  } else {
    amount = Math.round(absolute / 31536000);
    unit = "y";
  }
  return future ? `in ${amount}${unit}` : `${amount}${unit} ago`;
}

export function localizeRelativeTimes(root, now = Date.now()) {
  root.querySelectorAll?.("time[data-relative-time]").forEach(element => {
    const relative = relativeTimeLabel(element.dateTime || element.getAttribute("datetime"), now);
    if (relative) element.textContent = `${element.dataset.timeLabel || "Updated"} ${relative}`;
  });
}
