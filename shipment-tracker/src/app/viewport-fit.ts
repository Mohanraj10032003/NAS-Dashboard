/**
 * Makes the UI follow the REAL screen size.
 *
 * Some phone browsers show a page in "desktop mode": the page is laid out ~980px wide and then shrunk to fit the
 * screen, so every width-based breakpoint (max-width: 860px ...) thinks it is on a desktop and the text is tiny.
 * When the layout width is much bigger than the physical screen width, this:
 *   1. zooms the page up by that ratio (so text and buttons have a normal size), and
 *   2. re-evaluates the width breakpoints against the screen width (so the phone layout is used).
 * On a normal phone / tablet / laptop the two widths match and nothing happens.
 */
const original = new WeakMap<CSSMediaRule, string>();

/** true / false when the query only asks about width, null when it has other features (left alone) */
function widthQuery(text: string, w: number): boolean | null {
  if (/prefers|hover|pointer|orientation|height|print|aspect|resolution|color|only|not /i.test(text)) return null;
  let any = false;
  for (const part of text.split(',')) {
    const conds = [...part.matchAll(/\(\s*(min|max)-width\s*:\s*([\d.]+)px\s*\)/gi)];
    if (!conds.length) return null;
    if (conds.every((c) => (c[1].toLowerCase() === 'min' ? w >= +c[2] : w <= +c[2]))) any = true;
  }
  return any;
}

function patchBreakpoints(effectiveWidth: number | null): void {
  for (const sheet of Array.from(document.styleSheets)) {
    let rules: CSSRuleList;
    try {
      rules = sheet.cssRules;
    } catch {
      continue; // cross-origin sheet (Google Fonts)
    }
    for (const rule of Array.from(rules)) {
      if (!(rule instanceof CSSMediaRule)) continue;
      if (!original.has(rule)) original.set(rule, rule.media.mediaText);
      const orig = original.get(rule)!;
      let wanted = orig;
      if (effectiveWidth !== null) {
        const r = widthQuery(orig, effectiveWidth);
        if (r !== null) wanted = r ? 'all' : 'not all';
      }
      if (rule.media.mediaText !== wanted) rule.media.mediaText = wanted;
    }
  }
}

export function fitViewport(): void {
  const root = document.documentElement;
  let queued = false;

  const apply = () => {
    queued = false;
    const screenW = Math.min(screen.width || 0, screen.height || Infinity) > 0 ? screen.width : 0;
    const layoutW = window.innerWidth;
    const k = screenW ? layoutW / screenW : 1;
    if (screenW && screenW <= 700 && k > 1.3) {
      // the layout is wider than the screen: the browser shrinks it, so enlarge it back
      (root.style as any).zoom = String(Math.round(k * 100) / 100);
      patchBreakpoints(screenW);
    } else {
      (root.style as any).zoom = '';
      patchBreakpoints(null);
    }
  };
  const schedule = () => {
    if (queued) return;
    queued = true;
    requestAnimationFrame(apply);
  };

  apply();
  window.addEventListener('resize', schedule);
  window.addEventListener('orientationchange', schedule);
  // Angular adds each component's styles when the component is first shown
  new MutationObserver(schedule).observe(document.head, { childList: true });
}
