# Writing prompts for video (LTX 2.3 / 2.5)

Most bad takes are motion gone wrong: a subject moving the wrong way, a pose that bends wrong,
a prop that moves by itself, a camera that drifts. LTX takes the words literally and fills in
whatever they leave open, so say what moves, which way, how far, and what stays still.

## Pictures first
- LTX animates pictures well and draws them badly. Make the first frame with an image model
  (Z-Image, Flux, Qwen-Image, an SDXL checkpoint; an edit model such as Qwen-Image-Edit to keep a
  character or place from another picture), look at it, then animate it with an image-to-video
  template. Text-to-video only when the user asks for it.
- When where the shot ends matters, make the last frame too, from the first with an edit model,
  and use a first-and-last-frame template. The two frames agree on framing, light and everything
  that stays put, and differ only in what the shot changes: whatever differs between them is moved
  during the shot (a moon "upper left" in one and "above" in the other slides across the sky).
- An edit model changes a pose, a face, a turn or what someone holds well, and moves someone across
  the frame badly: asked for a girl "now beside the mailbox", it left her where she stood, and every
  take then walked her to the mailbox and back. When someone walks or moves far, animate the first
  frame alone and say in the prompt where they end up; look at a last frame before using it.
- A last frame pins the end: the video gets there early and holds it. A wave given a last frame
  with the hand up froze with the hand up for the last second and a half, twice. For an action
  that keeps going to the end (waving, walking, dancing, talking), give no last frame.
- The prompt says what happens from the picture. Call what is in it by the same words as the
  picture's prompt (the red fox, the wooden table), and don't describe a different scene.
- From a still pose LTX often holds the picture for a second or two before anything moves. Draw
  the first frame mid-action (a foot lifting, a hand already rising) and say the movement is
  already under way at the start.

## Sound and music
- LTX makes the sound with the video: speech, footsteps, wind, crowds, things being handled. Say
  what is heard in a sentence of its own. Words spoken go in quotes, with who says them and how
  ("she says quietly, in English: "We should go."").
- LTX is poor at music. Say "no music" in the prompt and make music with a music model (ACE-Step),
  then put the two together.
- A line that suggests moving gets acted out ("come on in" makes him walk to the camera).

## Shape of the prompt
- One paragraph in the present tense, 4 to 8 sentences, in the order things happen: the camera and
  shot size first, then the subject as it is at the start, then the action beat by beat, then how
  it ends, then the sound.
- Each action sentence has a verb that does something: walks, turns, lifts, sets down, looks up.
  Time words keep the order: "at first", "then", "a moment later", "by the end".
- About one main action per 3 to 4 seconds. More than that gets rushed, blurred or skipped; LTX 2.5
  times the clip by the prompt, so write pauses in ("she holds still for a beat").
- Make the clip as long as the action, no longer. A clip longer than its action holds the opening
  pose and then rushes: a wave LTX 2.5 times at about 3 s, in a 5 s clip, stood still for 3 s first;
  at 2.7 s the hold dropped to about 1 s. A wave or a turn takes 2 to 3 s, a few steps 3 to 4 s.
  LTX 2.5's duration predictor (LTXVDurationPredictor with the 3.8 MB
  ltx-2.5-duration-head-bf16.safetensors in models/model_patches) says how long a prompt's action
  takes.
- One main subject that moves, at most one small movement in the background. Crowded frames and
  chaotic motion turn into artifacts.

## Direction and distance
- Give the direction on screen: "walks from the left edge toward the right of the frame", "the
  ball rolls toward the camera". For turns, say both: "turns her head to her left, toward the
  window on the right of the frame".
- Name where it starts and ends: "from the doorway to the table", "lifts the cup from the saucer
  to her lips".
- Say how far and how fast: "two slow steps", "raises his hand to shoulder height", "slowly, over
  the whole shot".
- Keep paths simple. A sudden change of direction comes out unnatural.
- Name a side of the body by where it is in the picture: "the arm on the left side of the picture
  (his right arm)". Told "raises his right arm", both Qwen-Image-Edit and LTX raised the wrong one.

## Poses and bodies
- Describe the pose at the start (as the picture shows it) and the pose at the end: "she starts
  seated with her hands in her lap and ends standing, her right hand on the chair back".
- Name the body part that moves and how: "lifts his right arm to wave, palm open, then lowers it
  to his side".
- Say what doesn't move: "his feet stay planted", "she keeps both hands on the wheel".
- Animals and people stand on the ground: "stands on all four paws the whole time".
- Feelings as what the body does: "his shoulders drop and he looks at the floor", not "he is sad".
- Faces hold their likeness best at a steady distance; quick turns away and wide shots lose it.

## Characters and props
- Use the same words for each person and thing every time they come up.
- Say who holds what in which hand, and where things stay: "the umbrella stays in his left
  hand", "the vase on the shelf does not move".
- Things move only when someone moves them or they fall. Food, liquid and sparks come from a
  real source and go where gravity takes them: "twirls a forkful and lifts it to his mouth", not
  "noodles flying".
- No one and nothing appears or vanishes in the middle: "a dog walks in from the right edge".

## Camera
- Name the camera move in the first sentence, one move per shot: static locked-off camera, slow
  push in, pull back, pan left, tilt up, tracks alongside her, orbits 90 degrees to the right,
  handheld.
- Say where it ends: "pushes in until her face fills the frame".
- For a still shot: "static locked-off camera; the frame and the background do not move".

## When a take goes wrong
- Wrong direction: name both ends on screen and what stays still. Still wrong: motion tracks (below).
- Pose or body wrong: describe the start and end pose, fewer actions, a shorter shot, or a last
  frame that shows the end pose.
- A prop misbehaves: say who holds it, and that the rest stays where it is.
- The camera or background drifts: say the camera is locked off and what stays put; check that the
  first and last frames agree.
- Change the prompt first; a new seed alone repeats the same mistake.

## Exact motion: tracks and controls
- When the prompt gets the direction or the distance wrong, give it motion tracks: the Motion
  Control IC-LoRA (ComfyUI-LTXVideo's LTX Sparse Track Editor, LTX Draw Tracks and
  LTXAddVideoICLoRAGuide nodes; ltx-2.3-22b-ic-lora-motion-track-control-ref0.5.safetensors in
  models/loras, strength 1.0) follows tracks on the first frame: LTXVSparseTrackEditor's
  points_store, points in the picture's pixels spread evenly over the clip, 3 or 4 a track, on
  plausible paths (two tracks, head and chest, were enough). Told only "walks toward the left edge
  of the frame", a bunny walked at the camera, grew huge and became someone else; with the tracks
  he turned side-on and walked left at the same size. Where the prompt already works (a sip from a
  mug), tracks only set the timing. With tracks, the prompt describes the look, not the movement.
- A pose at a set moment: a guide in the middle (LTXVAddGuide, strength 1, frame index = the
  moment's share of the clip × (frames − 1)) between the same first and last picture.
- Exact poses and timing from a reference video: the Union Control IC-LoRA
  (ltx-2.3-22b-ic-lora-union-control-ref0.5) with skeletons from ComfyUI's own SDPose nodes
  (SDPoseKeypointExtractor, SDPoseDrawKeypoints; sdpose_wholebody_fp16). It copied a wave and its
  timing exactly. For a cartoon or animal character, make the reference video from a realistic
  human in the same pose and framing (Qwen-Image-Edit, then LTX): LTX moves real people better.
- The IC-LoRAs need the picture's sides to be multiples of 64, not 32: a 736 px height failed in
  LTXAddVideoICLoRAGuide.
- A loop: the same picture as the first frame (in place, strength 1) and as the last-frame guide
  (strength 1), then the last 8 frames crossfaded into the first 8 (an eased SolidMask ramp through
  ImageCompositeMasked) and the sound's ends faded and merged. Without the crossfade the motion
  slows almost to a stop around the seam.
- The user's templates for these, in their saved workflows under Templates/Video/LTX/Custom:
  video_furry_ltx2_5_flf2v (first and last frames, the first in place), _loop, _motion_tracks and
  _pose.

## Settings
- Distilled LTX runs about 8 steps with CFG near 1; a higher CFG doesn't help. Frames are 1 plus a
  multiple of 8, at 24 to 30 fps, at the picture's size (sides a multiple of 32).
- A negative prompt does nothing at CFG 1, which every distilled LTX template runs at: ComfyUI
  skips the pass that would use it. Say what should happen instead, in the prompt itself ("the
  camera does not move", "his feet stay on the ground").
- With a first and a last frame, the first frame put in place (LTXVImgToVideoInplace at strength
  0.7) starts moving at once, where a guide at frame 0 (LTXVAddGuide, as ComfyUI's own template has
  it) held it; a last-frame guide at 0.7 lands about as well as at 1.0.
- The prompt enhancer (Gemma) helps a short rough prompt; it picks its own shot type, so leave it
  off for a prompt written this way.
