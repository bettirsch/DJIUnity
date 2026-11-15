using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Cached visual projection. No camera, telemetry, physics or tracking dependencies.</summary>
public static class ShadowProjectionMath
{
    private static readonly int[,] Edges = { {0,1},{0,2},{0,4},{1,3},{1,5},{2,3},{2,6},{3,7},{4,5},{4,6},{5,7},{6,7} };
    public static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    public static bool TryProject(Vector3 point, Vector3 ray, ShadowReceiverPlane plane, out Vector3 projected)
    {
        projected = default;
        if (!plane.IsValid || !Finite(point) || !Finite(ray) || ray.sqrMagnitude < 1e-10f) return false;
        ray.Normalize();
        float denominator = Vector3.Dot(ray, plane.normal);
        if (Mathf.Abs(denominator) < 1e-5f) return false;
        float t = Vector3.Dot(plane.center - point, plane.normal) / denominator;
        if (!float.IsFinite(t) || t < -1e-5f) return false;
        projected = point + ray * Mathf.Max(t, 0);
        return Finite(projected);
    }

    public static List<Vector2> ProjectBox(Vector3[] corners, int start, Vector3 ray, ShadowReceiverPlane plane)
    {
        var points = new List<Vector2>(20);
        if (!plane.IsValid || !Finite(ray) || ray.sqrMagnitude < 1e-10f ||
            Mathf.Abs(Vector3.Dot(ray.normalized, plane.normal)) < 1e-5f) return points;
        for (int i = 0; i < 8; i++)
            if (TryProject(corners[start + i], ray, plane, out var p)) points.Add(plane.Coordinates(p));
        // Include caster/receiver intersections when a support crosses the ground plane.
        for (int i = 0; i < 12; i++)
        {
            var a = corners[start + Edges[i, 0]]; var b = corners[start + Edges[i, 1]];
            float da = Vector3.Dot(a - plane.center, plane.normal), db = Vector3.Dot(b - plane.center, plane.normal);
            if (da * db < 0) points.Add(plane.Coordinates(Vector3.LerpUnclamped(a, b, da / (da - db))));
        }
        return Hull(points);
    }

    private static readonly int[][] Faces = {
        new[] {0,1,3,2}, new[] {4,5,7,6}, new[] {0,1,5,4},
        new[] {2,3,7,6}, new[] {0,2,6,4}, new[] {1,3,7,5}
    };

    public static List<Vector3> ClipToFront(List<Vector3> polygon, ShadowReceiverPlane boundary)
    {
        var result = new List<Vector3>();
        if (polygon.Count == 0) return result;
        var a = polygon[polygon.Count - 1];
        float da = Vector3.Dot(a - boundary.center, boundary.normal);
        foreach (var b in polygon)
        {
            float db = Vector3.Dot(b - boundary.center, boundary.normal);
            if ((da >= 0) != (db >= 0)) result.Add(Vector3.LerpUnclamped(a, b, da / (da - db)));
            if (db >= 0) result.Add(b);
            a = b; da = db;
        }
        return result;
    }

    private static List<Vector2> ClipUv(List<Vector2> polygon, Func<Vector2, float> distance)
    {
        var result = new List<Vector2>();
        if (polygon.Count == 0) return result;
        var a = polygon[polygon.Count - 1]; float da = distance(a);
        foreach (var b in polygon)
        {
            float db = distance(b);
            if ((da >= 0) != (db >= 0)) result.Add(Vector2.LerpUnclamped(a, b, da / (da - db)));
            if (db >= 0) result.Add(b);
            a = b; da = db;
        }
        return result;
    }

    /// <summary>Clip the caster to the exterior air volume, then partition its shadow at the ground/wall seam.</summary>
    public static List<Vector2> ProjectBoxFirstSurface(Vector3[] corners, int start, Vector3 ray,
        ShadowReceiverPlane receiver, ShadowReceiverPlane ground, ShadowReceiverPlane wall)
    {
        var points = new List<Vector2>();
        if (corners == null || start < 0 || start + 7 >= corners.Length || !receiver.IsValid ||
            !ground.IsValid || !wall.IsValid || !Finite(ray) || ray.sqrMagnitude < 1e-10f) return points;
        foreach (var face in Faces)
        {
            var polygon = new List<Vector3>(4);
            foreach (int index in face) polygon.Add(corners[start + index]);
            if (polygon.Exists(p => !Finite(p))) return new List<Vector2>();
            polygon = ClipToFront(ClipToFront(polygon, ground), wall);
            foreach (var vertex in polygon)
                if (TryProject(vertex, ray, receiver, out var projected)) points.Add(receiver.Coordinates(projected));
        }
        var hull = Hull(points);
        // A ground hit behind the wall, or a wall hit below ground, occurs after
        // the other surface. Such hits must not appear on either mask twice.
        bool isGround = Vector3.Dot(receiver.normal, ground.normal) > .99f;
        var other = isGround ? wall : ground;
        hull = ClipUv(hull, uv => Vector3.Dot(receiver.Point(uv) - other.center, other.normal));
        hull = ClipUv(hull, uv => uv.x); hull = ClipUv(hull, uv => 1 - uv.x);
        hull = ClipUv(hull, uv => uv.y); hull = ClipUv(hull, uv => 1 - uv.y);
        return hull;
    }

    public static bool TryFirstReceiver(Vector3 point, Vector3 ray, ShadowReceiverPlane ground,
        ShadowReceiverPlane wall, out bool wallFirst, out Vector3 projected)
    {
        wallFirst = false; projected = default;
        if (!ground.IsValid || !wall.IsValid || !Finite(point) || Vector3.Dot(point - ground.center, ground.normal) < -1e-5f ||
            Vector3.Dot(point - wall.center, wall.normal) < -1e-5f) return false;
        bool g = TryProject(point, ray, ground, out var pg);
        bool w = TryProject(point, ray, wall, out var pw);
        if (!g && !w) return false;
        wallFirst = w && (!g || (pw - point).sqrMagnitude < (pg - point).sqrMagnitude);
        projected = wallFirst ? pw : pg;
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
    private static List<Vector2> Hull(List<Vector2> points)
    {
        points.RemoveAll(p => !float.IsFinite(p.x) || !float.IsFinite(p.y));
        points.Sort((a,b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        if (points.Count < 3) return new List<Vector2>();
        var result = new List<Vector2>();
        foreach (var p in points) { while (result.Count >= 2 && Cross(result[result.Count-2], result[result.Count-1], p) <= 0) result.RemoveAt(result.Count-1); result.Add(p); }
        int lower = result.Count;
        for (int i = points.Count - 2; i >= 0; i--) { var p = points[i]; while (result.Count > lower && Cross(result[result.Count-2], result[result.Count-1], p) <= 0) result.RemoveAt(result.Count-1); result.Add(p); }
        if (result.Count > 1) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void Rasterize(float[] mask, int width, int height, List<Vector2> polygon, float strength)
    {
        if (polygon.Count < 3 || strength <= 0) return;
        Vector2 min = polygon[0], max = min;
        foreach (var p in polygon) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(min.x) * width), 0, width-1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(max.x) * width), 0, width-1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(min.y) * height), 0, height-1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(max.y) * height), 0, height-1);
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
        {
            var p = new Vector2((x+.5f)/width, (y+.5f)/height); bool inside = true;
            for (int i = 0; i < polygon.Count; i++) if (Cross(polygon[i], polygon[(i+1)%polygon.Count], p) < -1e-7f) { inside = false; break; }
            if (inside) mask[y*width+x] = Mathf.Max(mask[y*width+x], strength);
        }
    }

    private static void Blur(float[] source, float[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
        {
            float sum = 0;
            int Index(int at) => horizontal ? line*width+at : at*width+line;
            for (int p = -radius; p <= radius; p++) if (p >= 0 && p < length) sum += source[Index(p)];
            for (int p = 0; p < length; p++)
            {
                target[Index(p)] = sum/(radius*2+1);
                int remove = p-radius, add = p+radius+1;
                if (remove >= 0) sum -= source[Index(remove)];
                if (add < length) sum += source[Index(add)];
            }
        }
    }

    public static byte[] Bake(RoofCanopyShadowSettings settings, ShadowReceiverPlane plane, out int width, out int height)
    {
        width = Mathf.Clamp(settings.resolution, 128, 1024);
        height = plane.IsValid ? Mathf.Clamp(Mathf.RoundToInt(width * plane.size.y/plane.size.x), 64, 1024) : 128;
        var mask = new float[width*height];
        if (!plane.IsValid || !Finite(settings.sunlightRayDirection) || settings.sunlightRayDirection.sqrMagnitude < 1e-10f) return new byte[mask.Length];
        int w = width, h = height;
        void Boxes(Vector3[] points, float strength) {
            for (int i = 0; i+7 < points.Length; i += 8) Rasterize(mask, w, h,
                settings.useFirstSurfaceProjection && settings.wallEnabled && settings.wallCalibrated
                    ? ProjectBoxFirstSurface(points, i, settings.sunlightRayDirection, plane, settings.Ground, settings.Wall)
                    : ProjectBox(points, i, settings.sunlightRayDirection, plane), Mathf.Clamp01(strength));
        }
        Boxes(settings.woodProxyCorners, settings.woodStrength);
        Boxes(settings.roofProxyCorners, settings.roofStrength);
        int radiusX = Mathf.Clamp(Mathf.RoundToInt(settings.softnessMeters/plane.size.x*width), 1, 24);
        int radiusY = Mathf.Clamp(Mathf.RoundToInt(settings.softnessMeters/plane.size.y*height), 1, 24);
        var work = new float[mask.Length];
        Blur(mask, work, width, height, radiusX, true); Blur(work, mask, width, height, radiusY, false);
        if (Vector3.Dot(plane.normal, Vector3.forward) > .99f)
        {
            foreach (var contact in settings.contacts)
            {
                var uv = plane.Coordinates(contact); float radius = .09f;
                int cx = Mathf.RoundToInt(uv.x*width), cy = Mathf.RoundToInt(uv.y*height);
                int rx = Mathf.CeilToInt(radius/plane.size.x*width), ry = Mathf.CeilToInt(radius/plane.size.y*height);
                for (int y = Mathf.Max(0,cy-ry); y <= Mathf.Min(height-1,cy+ry); y++) for (int x = Mathf.Max(0,cx-rx); x <= Mathf.Min(width-1,cx+rx); x++) {
                    float dx = ((x+.5f)/width-uv.x)*plane.size.x, dy = ((y+.5f)/height-uv.y)*plane.size.y;
                    float falloff = Mathf.Clamp01(1-Mathf.Sqrt(dx*dx+dy*dy)/radius);
                    mask[y*width+x] = Mathf.Max(mask[y*width+x], falloff*settings.contactStrength);
                }
            }
        }
        var result = new byte[mask.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            // A calibrated rear wall terminates the ground receiver, rather than letting
            // its projected image continue through the building and darken twice.
            if (settings.wallEnabled && settings.wallCalibrated && Vector3.Dot(plane.normal, Vector3.forward) > .99f) {
                var point = plane.Point(new Vector2((x+.5f)/width,(y+.5f)/height));
                var wall = settings.Wall; var uv = wall.Coordinates(point);
                if (wall.IsValid && (settings.useFirstSurfaceProjection || uv.x >= 0 && uv.x <= 1) && Vector3.Dot(point-wall.center,wall.normal) < 0) continue;
            }
            float edge = Mathf.Clamp01(Mathf.Min(Mathf.Min(x,width-1-x)/(float)(radiusX+1), Mathf.Min(y,height-1-y)/(float)(radiusY+1)));
            result[y*width+x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(mask[y*width+x]*settings.opacity*edge)*255);
        }
        return result;
    }
}
