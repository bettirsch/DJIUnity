using UnityEngine;

/// <summary>Identifies only the DJI camera using the explicit native-optical view.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class DjiOverlayCullingParity : MonoBehaviour
{
}
