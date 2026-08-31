# Building Reference Image

`BuildingReference.png` is the actual AR Foundation / ARCore tracking target. It is a deterministic, grayscale-safe, 1800 x 1800 pixel artwork with deliberately asymmetric, high-contrast local features. Regenerate it with:

```powershell
./Tools/GenerateBuildingReferenceImage.ps1
```

The target must be printed at exactly `180 mm x 180 mm`. Accordingly, `BuildingReferenceImageLibrary.asset` contains the single `BuildingReference` entry with `Specify Size` enabled and a physical width of `0.18 m`.

Use `Print/BuildingReference_A4.png` to print the target on an A4 portrait sheet. Print at `100%`, `Actual Size`, or the equivalent setting, and disable `Fit to page`. Measure the supplied 100 mm scale after printing before scanning.

For monitor testing, the library still assumes a physical target width of 180 mm. Either display `BuildingReference.png` at exactly 180 mm wide on the screen, measured with a ruler, or temporarily change the reference image library's physical width to the measured on-screen width. Merely zooming the image without updating the configured width produces incorrect AR scale and distance estimates.

`Tools > DJI > Configure Reference Image Tracking` always restores the scene wiring, the `BuildingReference` entry, `BuildingReference.png`, and the required `0.18 m` width. Build the Android player after changing the source artwork so ARCore rebuilds its runtime image database.
