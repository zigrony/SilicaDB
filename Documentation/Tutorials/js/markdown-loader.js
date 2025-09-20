async function loadMarkdown(file) {
  const res = await fetch(`content/${file}`);
  const text = await res.text();
  return marked.parse(text); // requires marked.js
}
