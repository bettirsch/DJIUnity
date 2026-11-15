using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class RoofCanopyPlacementTests
{
    private const string ResourcePath = "ARContent/RoofAssets/roof_canopy1";
    private GameObject _board;
    private GameObject _placement;

    [SetUp]
    public void SetUp()
    {
        _board = new GameObject("SharedReferenceContent");
        var create = typeof(ARContentFactory).GetMethod("CreateRoofCanopy", BindingFlags.Static | BindingFlags.NonPublic);
        _placement = (GameObject)create.Invoke(null, new object[] { _board.transform, 8 });
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_board);
    }

    [Test]
    public void ProductionFactory_LoadsCanopyWithoutCubeFallback_AndSeparatesPlacementFromModel()
    {
        Assert.That(Resources.Load<GameObject>(ResourcePath), Is.Not.Null);
        Assert.That(_placement.name, Is.EqualTo("RoofCanopyPlacement"));
        Assert.That(_placement.transform.parent, Is.EqualTo(_board.transform));
        Assert.That(_placement.transform.childCount, Is.EqualTo(1));
        AssertVector(_placement.transform.localScale, Vector3.one);
        Assert.That(Quaternion.Angle(_placement.transform.localRotation, Quaternion.identity), Is.LessThan(0.001f));
        AssertVector(Model.localPosition, Vector3.zero);
        AssertVector(Model.localScale, Vector3.one);
        Assert.That(Model.GetComponentsInChildren<MeshRenderer>().Length, Is.EqualTo(3));
    }

    [Test]
    public void ActualLeftRearUprightBottomVertex_IsAtBoardOrigin_NotBoundsCorner()
    {
        var wood = Model.Find("wood");
        var vertex = SemanticAnchorVertex(wood);
        var beforePlacement = Model.localRotation * Model.InverseTransformPoint(wood.TransformPoint(vertex));
        AssertVector(beforePlacement, new Vector3(0.099755391f, 0.014564678f, 0.086760163f));
        AssertVector(_placement.transform.localPosition, -beforePlacement);
        AssertVector(_board.transform.InverseTransformPoint(wood.TransformPoint(vertex)), Vector3.zero);

        // The rear upright ends above the front feet. Preserve that authored geometry.
        Assert.That(VertexBounds().min.z, Is.EqualTo(-0.059018f).Within(0.00001f));
    }

    [Test]
    public void ActualGeometry_RemainsFullScale_AndProjectsRightAndNegativeDepthFromSameAnchor()
    {
        var bounds = VertexBounds();
        AssertVector(bounds.size, new Vector3(4.972706f, 1.688028f, 2.379930f));
        AssertVector(bounds.min, new Vector3(-0.214899f, -1.651205f, -0.059018f));
        AssertVector(bounds.max, new Vector3(4.757807f, 0.036823f, 2.320912f));
        Assert.That(bounds.max.x, Is.GreaterThan(4f));
        Assert.That(bounds.min.y, Is.LessThan(-1.5f));
        AssertVector(_board.transform.localPosition, Vector3.zero);
        AssertVector(_board.transform.localScale, Vector3.one);
    }

    [Test]
    public void BaseOrientation_IsRxPositive90_WithoutY180OrExtraUnitConversion()
    {
        AssertVector(Model.localRotation * Vector3.right, Vector3.right);
        AssertVector(Model.localRotation * Vector3.up, Vector3.forward);
        AssertVector(Model.localRotation * Vector3.forward, Vector3.down);
        AssertVector(Model.localScale, Vector3.one);
    }

    [Test]
    public void ImportedChildTransformsMeshesAndMaterials_ArePreserved()
    {
        var source = Resources.Load<GameObject>(ResourcePath);
        foreach (Transform original in source.transform)
        {
            var actual = Model.Find(original.name);
            Assert.That(actual, Is.Not.Null);
            AssertVector(actual.localPosition, original.localPosition);
            AssertVector(actual.localScale, original.localScale);
            Assert.That(Quaternion.Angle(actual.localRotation, original.localRotation), Is.LessThan(0.001f));
            Assert.That(actual.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(original.GetComponent<MeshFilter>().sharedMesh));
            CollectionAssert.AreEqual(original.GetComponent<Renderer>().sharedMaterials, actual.GetComponent<Renderer>().sharedMaterials);
        }
    }

    [Test]
    public void LayerAndVisibility_ReachAllRenderersThroughPlacementWrapper()
    {
        foreach (var node in _placement.GetComponentsInChildren<Transform>())
            Assert.That(node.gameObject.layer, Is.EqualTo(8));
        ARContentFactory.SetRenderersEnabled(_placement, false);
        foreach (var renderer in _placement.GetComponentsInChildren<Renderer>())
            Assert.That(renderer.enabled, Is.False);
        ARContentFactory.SetRenderersEnabled(_placement, true);
        foreach (var renderer in _placement.GetComponentsInChildren<Renderer>())
            Assert.That(renderer.enabled, Is.True);
    }

    [Test]
    public void Anchor_RemainsBoardLocal_WhenParentHasAWorldTransform()
    {
        _board.transform.SetPositionAndRotation(new Vector3(3f, -2f, 4f), Quaternion.Euler(17f, 31f, -22f));
        var wood = Model.Find("wood");
        var vertex = SemanticAnchorVertex(wood);
        AssertVector(_board.transform.InverseTransformPoint(wood.TransformPoint(vertex)), Vector3.zero);
        AssertVector(Model.localScale, Vector3.one);
    }

    private Transform Model => _placement.transform.Find("BoardRoofCanopy");

    private Vector3 SemanticAnchorVertex(Transform wood)
    {
        // Mesh repair may reorder vertices; retain the same geometric anchor and assertions.
        var expected = new Vector3(0.099755391f, 0.014564678f, 0.086760163f);
        var vertices = wood.GetComponent<MeshFilter>().sharedMesh.vertices;
        var best = vertices[0];
        var distance = float.PositiveInfinity;
        foreach (var vertex in vertices)
        {
            var point = Model.localRotation * Model.InverseTransformPoint(wood.TransformPoint(vertex));
            var error = (point - expected).sqrMagnitude;
            if (error < distance) { best = vertex; distance = error; }
        }
        return best;
    }

    private Bounds VertexBounds()
    {
        var bounds = new Bounds();
        var first = true;
        foreach (var filter in Model.GetComponentsInChildren<MeshFilter>())
        foreach (var vertex in filter.sharedMesh.vertices)
        {
            var point = _board.transform.InverseTransformPoint(filter.transform.TransformPoint(vertex));
            if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
            else bounds.Encapsulate(point);
        }
        return bounds;
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(0.00001f), $"actual={actual:F8} expected={expected:F8}");
    }
}
