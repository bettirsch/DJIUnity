using UnityEngine;

/// <summary>
/// Stores T_world_reference: the physical BuildingReference board pose in the ARCore world.
/// Reference-board axes are +X right across the print, +Y toward the printed-board top, and
/// +Z outward from the printed surface. The ARTrackedImage-to-reference conversion happens once
/// at acquisition time; later systems must consume this frame without model-local Euler offsets.
/// </summary>
[DisallowMultipleComponent]
public sealed class PersistentReferenceFrame : MonoBehaviour
{
    private static PersistentReferenceFrame _instance;

    private bool _hasReferencePose;
    private Pose _worldFromReference = Pose.identity;

    public static PersistentReferenceFrame Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<PersistentReferenceFrame>();
                if (_instance == null)
                {
                    var serviceObject = new GameObject(nameof(PersistentReferenceFrame));
                    _instance = serviceObject.AddComponent<PersistentReferenceFrame>();
                }
            }

            return _instance;
        }
    }

    public bool HasReferencePose => _hasReferencePose;
    public Pose ReferenceWorldPose => _worldFromReference;
    public Matrix4x4 WorldFromReference => Matrix4x4.TRS(
        _worldFromReference.position,
        _worldFromReference.rotation,
        Vector3.one);
    public Matrix4x4 ReferenceFromWorld => WorldFromReference.inverse;

    public static bool TryGetExisting(out PersistentReferenceFrame persistentReferenceFrame)
    {
        persistentReferenceFrame = _instance != null
            ? _instance
            : FindFirstObjectByType<PersistentReferenceFrame>();
        return persistentReferenceFrame != null;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[Persistent Reference] DUPLICATE_SERVICE_DESTROYED");
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        Debug.Log("[Persistent Reference] SERVICE_READY");
    }

    /// <summary>
    /// Explicitly saves a new T_world_reference. Call this only after a reliable acquisition.
    /// </summary>
    public void SetReferencePose(Pose worldFromReference)
    {
        _worldFromReference = new Pose(
            worldFromReference.position,
            Quaternion.Normalize(worldFromReference.rotation));
        _hasReferencePose = true;
    }

    public void ResetReferencePose()
    {
        _hasReferencePose = false;
        _worldFromReference = Pose.identity;
        Debug.Log("[Persistent Reference] REFERENCE_FRAME_RESET");
    }
}
