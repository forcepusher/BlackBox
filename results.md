# Mockup bench: SVG against HTML on Qwen 3.8 27B (q6_k_xl)

2026-10-31, the user's LM Studio server, thinking on (template medium), temperature 1.0, top_p 0.95,
top_k 20, min_p 0, max_tokens 32000. System prompt: "You are a UI designer. Reply with only the
file's contents, no explanation." Each ask once as SVG and once as HTML, the same words but the ending.
Round 2 continues each conversation with the picture of its round-1 file and asks it to fix layout
problems (the look-and-fix loop the agent does with read_file). SVGs drawn by Sloperator's renderer
(Svg.Skia, as the app shows them); HTML by Edge headless at the asked size, scale 1. Every SVG was
also drawn by Edge: the two match apart from text anti-aliasing (0.4-4.5 % of pixels, all on glyph
edges), so no renderer faults.

Marks are 1-5 from looking at each picture: 5 nothing wrong, 4 one small fault, 3 visible faults.

| Ask | Format | Round | Mark | Defects | Seconds | Output tokens |
|---|---|---|---|---|---|---|
| A | SVG | 1 | 5 | None. Every part asked for; centred card; links and Continue in place. | 56 | 1839 |
| A | SVG | 2 | 5 | None (same design, moved a few px). | 130 | 3931 |
| A | HTML | 1 | 5 | None. | 52 | 1786 |
| A | HTML | 2 | 5 | None (it returned the same file). | 65 | 2011 |
| B | SVG | 1 | 3 | Nav icons: General's is an odd glyph, Notifications and Privacy have none, the shield sits between Privacy and Accounts. Descriptions wrap well (manual lines). | 138 | 4671 |
| B | SVG | 2 | 4 | General and Notifications icons fixed; Privacy still has none and the shield is still between Privacy and Accounts. | 117 | 3800 |
| B | HTML | 1 | 5 | None. Descriptions wrap, controls line up on the right. | 74 | 2535 |
| B | HTML | 2 | 5 | None. | 130 | 4039 |
| C | SVG | 1 | 5 | None worth a mark (status badges sit a pixel or two high in their rows). | 229 | 7859 |
| C | SVG | 2 | 5 | Same as round 1 (no visible change). | 193 | 6281 |
| C | HTML | 1 | 3 | Page overflows: the 6th order row is cut off at the bottom. Subtitle touches the top bar's border. Y-axis labels clipped by the axis line, and the axis scale doesn't match the bar values. | 103 | 3684 |
| C | HTML | 2 | 4 | All 6 rows fit now. Subtitle still touches the border, y-axis labels still clipped, scale still doesn't match the bars. | 136 | 4399 |
| D | SVG | 1 | 3 | Header call/video icons are garbled shapes; the phone home bar is drawn over the message field. Bubbles, wrapping and timestamps are good. | 169 | 5443 |
| D | SVG | 2 | 3 | Unchanged: icons still garbled, home bar still over the field. | 198 | 6293 |
| D | HTML | 1 | 3 | Input bar cut off at the bottom (field and send button clipped); back arrow tiny. Bubbles and wrapping good. | 89 | 2851 |
| D | HTML | 2 | 4 | Input bar fixed; the first bubble now hides half under the header, and an empty strip is left under the input bar. | 123 | 3829 |

## Totals (4 asks, out of 20)

| | Round 1 | Round 2 | Seconds r1 / r2 | Output tokens r1 / r2 |
|---|---|---|---|---|
| SVG | 16 | 17 | 592 / 638 | 19812 / 20305 |
| HTML | 16 | 18 | 318 / 454 | 10856 / 14278 |

Asks: A sign-in (1280x800, dark), B settings with wrapping descriptions (1280x800, dark),
C analytics dashboard (1440x900, light), D phone chat (390x844). Sheets: sheets/<ask>-sheet.png
(SVG r1 | SVG r2 | HTML r1 | HTML r2). Files: out/<ask>-<format>-r<round>.<svg|html>, its picture
.png (and .edge.png for an SVG drawn by Edge), the model's raw reply .raw.txt, seconds and tokens .json.