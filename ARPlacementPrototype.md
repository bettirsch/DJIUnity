# AR Placement Prototype

## What this prototype does

- Uses the Android phone AR session for horizontal-plane detection.
- Raycasts every frame from the screen centre against AR Foundation planes.
- Shows a placement indicator when a valid horizontal plane is available.
- Creates one anchor and one dummy object at a time.
- Lets the user reset the current placement and place again.
- Wires the `XR Origin`, `AR Session`, UI canvas and placement indicator directly into `Assets/Scenes/SampleScene.unity`.

## Scene wiring

- `SampleScene` now owns the `XR Origin`, `Camera Offset`, `AR Session`, `AR Placement Canvas` and `AR Placement EventSystem` objects.
- The placement controller lives on the `XR Origin` object and stores scene references for the AR managers, warning text, reticle and buttons.
- `DJICameraPoseDriver` is left in the scene for later DJI-localization work, but it is disabled for this prototype so phone AR tracking can own the camera transform.
- The legacy `TapToPlaceMarker` object is disabled so it does not compete with the AR Foundation workflow.

## Important DJI limitation

The visible background in this project is the DJI drone camera feed rendered through the custom OES + URP pipeline.
The AR Foundation session, however, tracks the Android phone camera.

That means:

- an AR Foundation raycast is in the phone tracking coordinate system;
- an AR Foundation anchor is in the phone tracking coordinate system;
- neither one is automatically registered to the visible DJI image.

So this prototype is only a phone-based AR placement test. It does **not** prove that the cube is attached to a point seen in the DJI camera feed.

## Why the prototype is isolated

The placement controller intentionally leaves the DJI video background pipeline in place and treats the AR session as a separate tracking source. This keeps the current prototype useful for interaction and anchor-lifecycle testing without claiming image-accurate drone placement.

## Likely future path to true DJI-aligned placement

Correct placement relative to the DJI image will need a shared coordinate system between:

- the phone AR session;
- the DJI aircraft body pose;
- the gimbal orientation;
- the DJI camera intrinsics and distortion model;
- the body-to-camera extrinsic transform;
- some world or building reference geometry.

Potential future inputs include:

- aircraft pose and heading;
- gimbal yaw, pitch and roll;
- camera calibration;
- surveyed geometry or a local building frame;
- visual marker localization;
- photogrammetry alignment;
- RTK or another external reference.

One practical future approach is to place a known visual marker on the building, detect it in the DJI video, define a local building coordinate system from that marker, and place content using a measured offset from that marker.

This task intentionally does **not** implement marker detection yet.
