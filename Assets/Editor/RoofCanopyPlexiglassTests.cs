using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class RoofCanopyPlexiglassTests
{
    private GameObject _content;
    private MeshRenderer Roof => Find("roof");
    private MeshRenderer Drainage => Find("drainage");
    private MeshRenderer Wood => Find("wood");

    [SetUp]
    public void SetUp() => _content = RoofCanopyLightingPreview.CreateContent();

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(_content);

    [Test]
    public void ProductionRoof_HasDedicatedPlexiglass_NotTheDrainageMaterial()
    {
        var material = Roof.sharedMaterial;
        Assert.That(material, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(RoofCanopyPlexiglassSetup.MaterialPath)));
        Assert.That(material, Is.Not.SameAs(Drainage.sharedMaterial));
        Assert.That(material.name, Is.EqualTo("Roof Plexiglass"));
        Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
        Assert.That(material.GetFloat("_Metallic"), Is.Zero);
        Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(.8f));
        Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(new Color(.85f,.92f,.96f,RoofCanopyPlexiglassSetup.SelectedAlpha)));
        Assert.That(material.GetTexture("_BaseMap"), Is.Null);
        Assert.That(material.GetTexture("_BumpMap"), Is.Null);
    }

    [Test]
    public void TransparentAlpha_PreservesHighlights_AndDoesNotWriteOpaqueDepth()
    {
        var material = Roof.sharedMaterial;
        Assert.That(material.GetFloat("_Surface"), Is.EqualTo(1f));
        Assert.That(material.GetFloat("_Blend"), Is.Zero);
        Assert.That(material.GetTag("RenderType", false), Is.EqualTo("Transparent"));
        Assert.That(material.renderQueue, Is.EqualTo((int)RenderQueue.Transparent));
        Assert.That(material.GetFloat("_AlphaClip"), Is.Zero);
        Assert.That(material.GetFloat("_ZWrite"), Is.Zero);
        Assert.That(material.GetShaderPassEnabled("DepthOnly"), Is.False);
        Assert.That(material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.True);
        Assert.That(material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), Is.False);
        Assert.That(material.GetFloat("_BlendModePreserveSpecular"), Is.Zero);
        Assert.That(material.GetFloat("_SrcBlend"), Is.EqualTo((float)BlendMode.SrcAlpha));
        Assert.That(material.GetFloat("_DstBlend"), Is.EqualTo((float)BlendMode.OneMinusSrcAlpha));
        Assert.That(material.GetFloat("_EnvironmentReflections"), Is.EqualTo(1f));
        Assert.That(material.GetFloat("_SpecularHighlights"), Is.EqualTo(1f));
        Assert.That(Roof.sortingOrder, Is.Zero);
        Assert.That(material.renderQueue, Is.GreaterThan(Drainage.sharedMaterial.renderQueue));
    }

    [Test]
    public void Roof_IsDoubleSided_AndNoLongerCastsAnOpaqueShadow()
    {
        Assert.That(Roof.sharedMaterial.GetFloat("_Cull"), Is.EqualTo((float)CullMode.Off));
        Assert.That(Roof.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
        Assert.That(Roof.sharedMaterial.GetShaderPassEnabled("ShadowCaster"), Is.False);
        Assert.That(Roof.receiveShadows, Is.True);
        Assert.That(Roof.sharedMaterial.GetFloat("_ReceiveShadows"), Is.EqualTo(1f));
        Assert.That(Drainage.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
        Assert.That(Wood.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
    }

    [Test]
    public void Drainage_RemainsOpaqueAluminiumWithOriginalBaseAndNormalMaps()
    {
        var material = Drainage.sharedMaterial;
        Assert.That(material, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(RoofCanopyLightingSetup.AluminiumPath)));
        Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(.8f));
        Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(.5f));
        Assert.That(material.GetFloat("_Surface"), Is.Zero);
        Assert.That(material.GetTexture("_BaseMap").name, Is.EqualTo("Aluminium_scratches_BaseColor"));
        Assert.That(material.GetTexture("_BumpMap").name, Is.EqualTo("Aluminium_scratches_Normal"));
        Assert.That(material.GetFloat("_BumpScale"), Is.EqualTo(1f));
    }

    [Test]
    public void Wood_KeepsNaturalWoodAndNonTextureMaterialInputs()
    {
        var material = Wood.sharedMaterial;
        Assert.That(material.name, Is.EqualTo("Natural Wood"));
        Assert.That(material.GetTexture("_BaseMap").name, Is.EqualTo("natural_wood_07_color_contrast_2p0"));
        Assert.That(material.GetFloat("_Metallic"), Is.Zero);
        Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(.5f));
        Assert.That(material.GetTexture("_BumpMap"), Is.Null);
        Assert.That(material.GetTexture("_OcclusionMap"), Is.Null);
    }

    [Test]
    public void WoodContrastVariant_PreservesMaterialIdentity_Uvs_AndMobileTextureBudget()
    {
        const string folder = "Assets/Resources/ARContent/RoofAssets/textures/";
        var material = Wood.sharedMaterial;
        var original = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "natural_wood_07_color.jpg");
        var selected = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "natural_wood_07_color_contrast_2p0.png");
        Assert.That(original, Is.Not.Null);
        Assert.That(selected, Is.Not.Null);
        Assert.That(material.GetTexture("_BaseMap"), Is.SameAs(selected));
        Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId), Is.True);
        Assert.That(guid, Is.EqualTo("b3424cae4fbc33a4d9a425c85e2dddf7"));
        Assert.That(localId, Is.EqualTo(7243282191679225090L));
        Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(Color.white));
        Assert.That(material.GetTextureScale("_BaseMap"), Is.EqualTo(Vector2.one));
        Assert.That(material.GetTextureOffset("_BaseMap"), Is.EqualTo(Vector2.zero));
        Assert.That(selected.width, Is.EqualTo(original.width).And.EqualTo(2048));
        Assert.That(selected.height, Is.EqualTo(original.height).And.EqualTo(2048));
        Assert.That(selected.mipmapCount, Is.EqualTo(original.mipmapCount).And.GreaterThan(1));
        Assert.That(selected.format, Is.EqualTo(original.format));
        Assert.That(selected.filterMode, Is.EqualTo(original.filterMode));
        Assert.That(selected.anisoLevel, Is.EqualTo(original.anisoLevel));
        Assert.That(selected.wrapMode, Is.EqualTo(original.wrapMode));
        var sourceImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(original));
        var selectedImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(selected));
        Assert.That(selectedImporter.sRGBTexture, Is.EqualTo(sourceImporter.sRGBTexture).And.True);
        Assert.That(selectedImporter.mipmapEnabled, Is.True);
        Assert.That(selectedImporter.textureCompression, Is.EqualTo(sourceImporter.textureCompression));
        Assert.That(selectedImporter.GetAutomaticFormat("Android"), Is.EqualTo(sourceImporter.GetAutomaticFormat("Android")));
        Assert.That(selectedImporter.maxTextureSize, Is.EqualTo(sourceImporter.maxTextureSize));
    }

    [TestCase(.30f)]
    [TestCase(.40f)]
    [TestCase(.50f)]
    [TestCase(.60f)]
    public void AlphaVariants_OnlyChangeOpacity_NotBlendOrGeometry(float alpha)
    {
        var material = RoofCanopyPlexiglassSetup.CreateMaterial(alpha);
        try
        {
            Assert.That(material.GetColor("_BaseColor").a, Is.EqualTo(alpha));
            Assert.That(material.GetFloat("_Metallic"), Is.Zero);
            Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(.8f));
            Assert.That(material.GetFloat("_ZWrite"), Is.Zero);
            Assert.That(material.renderQueue, Is.EqualTo(3000));
            Assert.That(material.GetFloat("_Cull"), Is.Zero);
        }
        finally { Object.DestroyImmediate(material); }
    }

    private MeshRenderer Find(string name) => _content.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == name);
}
