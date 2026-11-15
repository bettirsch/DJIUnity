using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

public sealed class RoofCanopyMirrorTests
{
    private GameObject content;
    [SetUp] public void SetUp()=>content=RoofCanopyLightingPreview.CreateContent();
    [TearDown] public void TearDown()=>Object.DestroyImmediate(content);
    private MeshFilter Filter(string name)=>content.GetComponentsInChildren<MeshFilter>().Single(f=>f.name==name);

    // Source landmarks measured before reflection, asserted by geometry rather
    // than historical vertex indices. Preserve +X and height; reverse depth.
    [TestCase("wood",.435753018f,-1.094939828f,2.056363106f)]
    [TestCase("wood",.087352999f,-1.007283807f,2.087957859f)]
    [TestCase("wood",.027808890f,-.046134181f,.090789281f)]
    [TestCase("roof",-.118103907f,.031853396f,2.306955814f)]
    [TestCase("roof",.106430061f,.033297379f,2.289777517f)]
    [TestCase("roof",4.530791759f,-1.574515462f,2.202180862f)]
    [TestCase("drainage",-.141650170f,-.281319469f,1.861536384f)]
    [TestCase("drainage",-.121661879f,-1.568466187f,2.005828142f)]
    [TestCase("drainage",.045133382f,-1.524038672f,2.135998964f)]
    public void AllComponents_ReflectDepthWithoutReversingLengthOrHeight(string name,float x,float y,float z)
    {
        var f=Filter(name);var expected=new Vector3(x,y,z);
        Assert.That(f.sharedMesh.vertices.Min(v=>Vector3.Distance(f.transform.TransformPoint(v),expected)),Is.LessThan(.000003f));
    }

    [Test]
    public void SamePhysicalCorner_StillAtBoardOrigin_WithOriginalPlacementAndScale()
    {
        var model=content.GetComponentsInChildren<Transform>().Single(t=>t.name=="BoardRoofCanopy");
        Assert.That(Vector3.Distance(model.parent.localPosition,new Vector3(-.099755391f,-.014564678f,-.086760163f)),Is.LessThan(.0000001f));
        Assert.That(model.localScale,Is.EqualTo(Vector3.one));Assert.That(model.parent.localScale,Is.EqualTo(Vector3.one));
        Assert.That(Quaternion.Angle(model.localRotation,Quaternion.Euler(90,0,0)),Is.LessThan(.001f));
        var wood=Filter("wood");Assert.That(wood.sharedMesh.vertices.Min(v=>wood.transform.TransformPoint(v).magnitude),Is.LessThan(.000001f));
        Assert.That(content.GetComponentsInChildren<MeshFilter>().Any(f=>f.sharedMesh.name=="Cube"),Is.False);
    }

    [Test]
    public void MaterialIdentitiesAndTransparency_AreUnchanged()
    {
        var roof=Filter("roof").GetComponent<MeshRenderer>();var drainage=Filter("drainage").GetComponent<MeshRenderer>();var wood=Filter("wood").GetComponent<MeshRenderer>();
        Assert.That(roof.sharedMaterial.name,Is.EqualTo("Roof Plexiglass"));Assert.That(roof.sharedMaterial.GetFloat("_Surface"),Is.EqualTo(1));
        Assert.That(roof.shadowCastingMode,Is.EqualTo(ShadowCastingMode.Off));Assert.That(roof.sharedMaterial.GetFloat("_Cull"),Is.Zero);
        Assert.That(drainage.sharedMaterial.name,Is.EqualTo("Aluminium scratches"));Assert.That(drainage.sharedMaterial.GetFloat("_Metallic"),Is.EqualTo(.8f));
        Assert.That(wood.sharedMaterial.name,Is.EqualTo("Natural Wood"));Assert.That(wood.sharedMaterial.GetTexture("_BaseMap").name,Is.EqualTo("natural_wood_07_color_contrast_2p0"));
        Assert.That(AssetDatabase.AssetPathToGUID("Assets/Resources/ARContent/RoofAssets/roof_canopy_adjusted_pivot.fbx"),Is.EqualTo("b3424cae4fbc33a4d9a425c85e2dddf7"));
        Assert.That(AssetDatabase.AssetPathToGUID("Assets/Resources/ARContent/RoofAssets/roof_canopy1.prefab"),Is.EqualTo("603d57f1976837249985e1b5b2ca37c4"));
    }

    [TestCase("wood"),TestCase("roof"),TestCase("drainage")]
    public void ImportedUvAndTangentFrames_AreFiniteAndValid(string name)
    {
        var filter=Filter(name);var m=filter.sharedMesh;var ns=m.normals;var ts=m.tangents;var uv=m.uv;
        var matrix=filter.transform.localToWorldMatrix;var normalMatrix=matrix.inverse.transpose;
        Assert.That(uv.Length,Is.EqualTo(m.vertexCount));Assert.That(ts.Length,Is.EqualTo(m.vertexCount));
        for(int i=0;i<ts.Length;i++){
            Assert.That(float.IsFinite(uv[i].x)&&float.IsFinite(uv[i].y),Is.True);
            var t=new Vector3(ts[i].x,ts[i].y,ts[i].z);
            Assert.That(float.IsFinite(t.x)&&float.IsFinite(t.y)&&float.IsFinite(t.z),Is.True);
            Assert.That(t.magnitude,Is.EqualTo(1).Within(.0001f));Assert.That(Mathf.Abs(ts[i].w),Is.EqualTo(1));
            // The validated authored wood/drainage already have nonorthogonal
            // imported tangents. Check a finite, nondegenerate shader frame;
            // do not introduce a new material/normal-map redesign requirement.
            var tangentWorld=matrix.MultiplyVector(t).normalized;
            var normalWorld=normalMatrix.MultiplyVector(ns[i]).normalized;
            Assert.That(Vector3.Cross(normalWorld,tangentWorld).magnitude,Is.GreaterThan(.0001f));
        }
    }
}
