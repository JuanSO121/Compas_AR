using Unity.Sentis;
using UnityEngine;

public class TestShape : MonoBehaviour
{
    public ModelAsset modelAsset;

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);

        foreach (var o in model.outputs)
        {
            Debug.Log("Output name: " + o.name);
        }
    }
}