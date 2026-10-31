Avalonia 12 visual check, round 3: develop (aa8e763) vs feature/avalonia-12 (1c012ca), published
single-file exes, same scripted steps, same home folder and paths, window 1127x1067 physical (1902 wide
once a side panel opens), laptop at 125%. Captured with PrintWindow; the app was fed by a stand-in model
(run_shell, read_file pic.png / anim.gif / doc.pdf, write_file notes.md, then a markdown reply naming doc.pdf).
Settings' OCR language value and its footer path are painted magenta in every Settings image.

develop/ and branch/  same file names in both:
  00-start.png              start screen before sending
  01-file-changes.png       main window after the run, File changes opened by itself
  02-history-1.png          History list
  03-servers-1.png          Servers list
  04-side-panel-list-1.png  Side panel dropdown open
  05-chat-end.png           Side panel None, chat at its end
  06-chat-top.png           chat scrolled to the top
  07-chat-middle.png        chat scrolled to 45 %
  08-settings.png           Settings, General
  09-inspector-png.png      File inspector on pic.png (Inspect on its card)
  10-inspector-gif.png      File inspector on anim.gif
  11-inspector-pdf.png      File inspector on doc.pdf
  12-viewer.png             full-screen viewer opened from the picture (closed with Esc afterwards)
  13-after-viewer.png       main window after the viewer closed
  NN-*.txt                  every interactive control's box (UIA, physical px from the window's corner)
  NN-*-centre.txt           every word/control whose capitals' middle is more than 0.5 px from its box's
                            middle (+ = low), from .dev centre rig texts.ps1
  hints/                    main-*/settings-*-{rest,hover,focus,focushover}.png: each empty field's hint
                            with the cursor away, on it, focused, focused under it (real mouse);
                            settings-general/advanced.png + .txt boxes + -centre.txt
hints.txt         the hint colours read from hints/ for both builds
centre-compare.txt  every branch text off by more than 0.5 px, with develop's offset for the same one
grid-compare.txt    boxes and gaps on the 4 px grid (logical, origin 1 px in) in develop and off it on the branch
diff/<shot>.png   develop | branch | differing pixels (red, tolerance 12) over dimmed develop
diff/report.txt   share of differing pixels and every region of them (x y w h, physical px)
zoom/             develop | branch crops, magnified
Expected differences: clock times, "model N ms", "Done in", "Worked for", the animated GIF frame and the logo blob.
Round 3 additions:
  gif-burst-N.png (develop/, branch/)  ten captures of the chat's animated GIF 70 ms apart, to pair equal frames
  picture-compare.txt   exact differences in the picture areas (inspector PNG/GIF/PDF, chat PNG/GIF/PDF peek)
  drawn-compare.txt     controls' drawn top/bottom edges (from the PNG) against their UIA boxes, and their
                        words' capitals' middle against the drawn middle, both builds
