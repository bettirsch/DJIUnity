# Building Reference Image

`BuildingReferenceImageLibrary.asset` is the ARCore image-tracking library used by the phone scan scene. It currently contains `BuildingReference`, backed by `BuildingReferencePlaceholder.png`.

The placeholder is only an implementation target. Before field use, replace it with the final printed artwork and set the entry's physical width to the measured printed width in metres. The placeholder is currently configured as `0.42 m` wide and square; that is not a final project measurement.

To replace the image in Unity:

1. Import the final flat, non-glossy, high-detail artwork into this folder.
2. Open `BuildingReferenceImageLibrary.asset` and replace the `BuildingReference` texture.
3. Set the exact printed width in the library entry. Unity derives the height from the artwork aspect ratio.
4. Run `Tools > DJI > Configure Reference Image Tracking` to reapply the scan-scene wiring.
5. Build the Android player so ARCore generates the runtime image database from the new source texture.
