using UnityEngine;
using UnityEngine.SceneManagement;

public class GoTo : MonoBehaviour
{
    public void LoadSampleScene()
    {
        SceneManager.LoadScene("SampleScene"); // exact scene name
    }
}
