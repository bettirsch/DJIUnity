# AprilTag CPU Camera Calibration

`PhoneAprilTagScanController` runs PnP on AR Foundation's unrotated `640x480`
RGBA conversion of the ARCore CPU image. An offline calibration is valid only
when the source images have this exact resolution, no crop, no mirror and no
display rotation.

1. Capture at least 20 sharply focused ChArUco or checkerboard images from
   that exact CPU-image conversion, covering the image center and all edges.
2. Calibrate with OpenCV using the Brown-Conrady model and save `K` plus the
   OpenCV coefficient order `[k1, k2, p1, p2, k3]`.
3. Fill `Assets/Resources/AprilTagCpuCameraCalibration.json` with those
   values and change `enabled` to `true`.
4. Rebuild and check the `Image-space reprojection` logs. The profile must be
   ignored if the active CPU image is not exactly `cpuImageWidth` by
   `cpuImageHeight`.

The profile deliberately does not consume Android Camera2 `LENS_DISTORTION`
directly. AR Foundation does not identify the physical Camera2 stream ARCore
selected for a CPU image, and Android's distortion coordinate convention is
not the OpenCV pixel-space coefficient convention without a verified mapping.
