import MarkdownIt from "markdown-it";

/**
 * Markdown for note text. Raw HTML is disabled, so the output is safe to put in
 * `v-html`: markdown-it escapes HTML and rejects `javascript:`/`vbscript:` links.
 */
const md = new MarkdownIt({
  html: false,
  linkify: true, // bare URLs become links
  breaks: true, // a single newline is a line break, like in a chat box
  typographer: false,
});

// Links open in a new tab without giving the target page access to this one.
const defaultLinkOpen =
  md.renderer.rules.link_open ?? ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options));
md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  const token = tokens[idx]!;
  token.attrSet("target", "_blank");
  token.attrSet("rel", "noopener noreferrer");
  return defaultLinkOpen(tokens, idx, options, env, self);
};

// Images load lazily and don't leak the app's URL to the image host.
const defaultImage = md.renderer.rules.image!;
md.renderer.rules.image = (tokens, idx, options, env, self) => {
  const token = tokens[idx]!;
  token.attrSet("loading", "lazy");
  token.attrSet("referrerpolicy", "no-referrer");
  return defaultImage(tokens, idx, options, env, self);
};

const cache = new Map<string, string>();

/** Renders note text to HTML (memoized; notes re-render often while the feed refreshes). */
export function renderMarkdown(text: string): string {
  let html = cache.get(text);
  if (html === undefined) {
    html = md.render(text);
    if (cache.size > 500) cache.clear();
    cache.set(text, html);
  }
  return html;
}
