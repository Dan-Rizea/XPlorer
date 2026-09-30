# Inspector browser regression results

Browser: Chrome/154.0.8037.58

Configuration: default browser security and site isolation.

86/86 extended scenarios passed; 415 successful assertions including the original smoke suite.

Original suite also covers nested cross-origin frames, document capture, nested shadow roots, detached frames, frame-specific actions with another tab open, navigation, and cancellation.

| Scenario | Result | Details |
|---|---|---|
| opaque sibling cover | PASS |  |
| transparent sibling cover | PASS |  |
| pointer-events:none input | PASS |  |
| visibility:hidden input | PASS |  |
| opacity:0 input | PASS |  |
| disabled input | PASS |  |
| readonly input | PASS |  |
| display:none input | PASS |  |
| zero-size input | PASS |  |
| scaled wrapper | PASS |  |
| rotated wrapper | PASS |  |
| RTL page | PASS |  |
| covered input type=text | PASS |  |
| covered input type=password | PASS |  |
| covered input type=email | PASS |  |
| covered input type=number | PASS |  |
| covered input type=date | PASS |  |
| covered input type=range | PASS |  |
| covered input type=checkbox | PASS |  |
| covered input type=radio | PASS |  |
| covered input type=file | PASS |  |
| covered input type=color | PASS |  |
| covered input type=hidden | PASS |  |
| covered input type=search | PASS |  |
| covered input type=tel | PASS |  |
| covered input type=url | PASS |  |
| Down reaches covered textarea | PASS |  |
| Down reaches covered select | PASS |  |
| Down reaches covered button | PASS |  |
| Down reaches covered contenteditable=true | PASS |  |
| Down reaches covered contenteditable empty | PASS |  |
| Down reaches covered contenteditable plaintext-only | PASS |  |
| selector: both quote types in ID | PASS |  |
| selector: CSS punctuation ID | PASS |  |
| selector: Unicode ID | PASS |  |
| selector: test ID quotes | PASS |  |
| selector: name only | PASS |  |
| selector: placeholder only | PASS |  |
| selector: aria label only | PASS |  |
| selector: unique class only | PASS |  |
| selector: duplicate ID | PASS |  |
| selector: no attributes | PASS |  |
| XPath text with quotes and whitespace | PASS |  |
| SVG element selectors | PASS |  |
| multiple overlapping input layers and wraparound | PASS |  |
| tiny pointer jitter preserves selected layer | PASS |  |
| moving to another control resets layer selection | PASS |  |
| click without preceding mousemove | PASS |  |
| ordinary wheel scroll remains usable | PASS |  |
| scrolled overflow container | PASS |  |
| cover implemented by pseudo-element | PASS |  |
| modal dialog in top layer | PASS |  |
| dynamic insertion while indicating | PASS |  |
| replacement after layer list was built | PASS |  |
| repeated injection leaves one overlay and one capture | PASS |  |
| cancel restores native click handlers | PASS |  |
| 25 rapid start and cancel cycles | PASS |  |
| 10,000-node page layer scan | PASS |  |
| dynamic frame: same-origin | PASS |  |
| dynamic frame: cross-origin | PASS |  |
| dynamic frame: srcdoc | PASS |  |
| dynamic frame: sandbox opaque origin | PASS |  |
| dynamic frame: shadow-hosted iframe | PASS |  |
| slotted input | PASS |  |
| hidden input inside shadow | PASS |  |
| closed shadow host | PASS |  |
| all actions on a captured text input | PASS |  |
| missing saved element gives an action error | PASS |  |
| root selection: body | PASS |  |
| root selection: html | PASS |  |
| root selection: #document | PASS |  |
| document Down returns the html child | PASS |  |
| document without a body | PASS |  |
| empty document | PASS |  |
| SVG hierarchical selector without identifying attributes | PASS |  |
| MathML namespace selector | PASS |  |
| full-page fixed cover over an input | PASS |  |
| body replaced by SPA while indicating | PASS |  |
| modal opened after indication starts | PASS |  |
| native popover input | PASS |  |
| top document navigation while indicating | PASS |  |
| frame transitions from same origin to cross origin | PASS |  |
| captured tab closure reports an error | PASS |  |
| rapid cancel then restart cannot capture stale results | PASS |  |
| frame hover activates real CSS hover state | PASS |  |
| document rejects text-input actions without altering DOM | PASS |  |
