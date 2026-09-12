using UnityEngine;

/// <summary>
/// Stores T_world_board: the physical reference board pose in the ARCore world.
/// Reference-board axes are +X right across the print, +Y toward the printed-board top, and
/// +Z outward from the printed surface. The ARTrackedImage-to-reference conversion happens once
/// at acquisition time; later systems must consume this frame without model-local Euler offsets.
/// </summary>
[DisallowMultipleComponent]
public sealed class PersistentReferenceFrame : MonoBehaviour
{
    private static PersistentReferenceFrame _instance;

    private bool _hasBoardPose;
    private Pose _worldFromBoard = Pose.identity;

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

    public bool HasBoardPose => _hasBoardPose;
    public Pose BoardWorldPose => _worldFromBoard;
    public Matrix4x4 WorldFromBoard => ReferenceFrameTransforms.WorldFromBoard(_worldFromBoard);
    public Matrix4x4 BoardFromWorld => ReferenceFrameTransforms.BoardFromWorld(_worldFromBoard);

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
    /// Explicitly saves a new T_world_board. Call this only after a reliable acquisition.
    /// </summary>
    public void SetWorldFromBoard(Pose worldFromBoard)
    {
        _worldFromBoard = new Pose(
            worldFromBoard.position,
            Quaternion.Normalize(worldFromBoard.rotation));
        _hasBoardPose = true;
    }

    public void ResetReferencePose()
    {
        _hasBoardPose = false;
        _worldFromBoard = Pose.identity;
        Debug.Log("[Persistent Reference] REFERENCE_FRAME_RESET");
    }
}
