// File: ObstacleSegmentationWorker.cs
// ✅ v8 — FlipX independiente para corregir espejo horizontal
//
// ============================================================================
//  CAMBIOS v7 → v8
// ============================================================================
//
//  FIX — _FlipX añadido al shader y API pública
//    Con _FlipY=1 y rotación 90° la máscara quedaba espejada horizontalmente.
//    Ahora _FlipX y _FlipY son parámetros independientes del shader.
//    Por defecto: flipY=true, flipX=true (Pixel 5 portrait cámara trasera).
//
//    Para calibrar en runtime usar ContextMenu en SegmentationController:
//      "Flip X ON/OFF" y "Flip Y ON/OFF"
//
//  SIN CAMBIOS — IMAGE_SIZE=312, pipeline GPU, tensor reutilizado,
//    PollResult, fallback CPU, API pública restante.

using System;
using Unity.Sentis;
using UnityEngine;

namespace IndoorNavAR.Segmentation
{
    public class ObstacleSegmentationWorker : IDisposable
    {
        public const int CLASS_BACKGROUND = 0;
        public const int CLASS_FLOOR      = 1;
        public const int CLASS_OBSTACLE   = 2;
        public const int CLASS_WALL       = 3;

        public const int IMAGE_SIZE = 256;
        private const int MAX_POLL_FAILS = 60;

        private readonly ModelAsset  _modelAsset;
        private          Model       _runtimeModel;
        private          Worker      _worker;
        private          BackendType _backendType;

        private Tensor<float> _inputTensor;
        private bool          _tensorReady = false;

        private RenderTexture _rotatedRT;
        private Material      _rotateMat;
        private int           _rotationDegrees;
        private bool          _flipY = true;
        private bool          _flipX = true;
        private bool          _rotateMatReady;

        public bool IsReady { get; private set; }
        public bool IsBusy  { get; private set; }

        private bool   _debugShapeLogged = false;
        private int    _pollFailCount    = 0;
        private string _outputName       = null;

        public int[]  MaskData      { get; private set; }
        public int    MaskWidth     => IMAGE_SIZE;
        public int    MaskHeight    => IMAGE_SIZE;
        public float  ObstacleRatio { get; private set; }
        public float  FloorRatio    { get; private set; }
        public float  WallRatio     { get; private set; }

        public event Action OnInferenceComplete;

        public static ObstacleSegmentationWorker Instance { get; private set; }

        public ObstacleSegmentationWorker(ModelAsset modelAsset,
                                          BackendType backend = BackendType.GPUCompute,
                                          int rotationDegrees = 90,
                                          bool flipY = true,
                                          bool flipX = true)
        {
            Instance = this;

            if (modelAsset == null)
            {
                Debug.LogError("[SegWorker] ModelAsset es null.");
                return;
            }

            _modelAsset      = modelAsset;
            _backendType     = backend;
            _rotationDegrees = rotationDegrees;
            _flipY           = flipY;
            _flipX           = flipX;
            MaskData         = new int[IMAGE_SIZE * IMAGE_SIZE];

            InitializeRotationResources();
            InitializeWorker(backend);
        }

        private void InitializeRotationResources()
        {
            _rotatedRT = new RenderTexture(IMAGE_SIZE, IMAGE_SIZE, 0,
                                           RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
            _rotatedRT.Create();

            Shader shader = Shader.Find("Hidden/SegRotate");

            if (shader == null)
            {
                Debug.LogWarning(
                    "[SegWorker] ⚠️ Hidden/SegRotate NO encontrado.\n" +
                    "► Copia SegRotate.shader a Assets/IndoorNavAR/Resources/\n" +
                    "  O añádelo en Project Settings → Graphics → Always Included Shaders.\n" +
                    "► Intentando compilar desde string como fallback...");
                shader = ShaderUtil_CreateFromString();
            }

            if (shader != null)
            {
                _rotateMat      = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _rotateMatReady = true;
                _rotateMat.SetFloat("_FlipY", _flipY ? 1f : 0f);
                _rotateMat.SetFloat("_FlipX", _flipX ? 1f : 0f);
                Debug.Log($"[SegWorker] ✅ Shader listo. flipY={_flipY} flipX={_flipX}");
            }
            else
            {
                Debug.LogError("[SegWorker] ❌ No se pudo obtener shader de rotación.");
            }
        }

        private static Shader ShaderUtil_CreateFromString()
        {
            const string src = @"Shader ""Hidden/SegRotate""
{
    Properties
    {
        _MainTex  (""Texture"",  2D)    = ""white"" {}
        _Rotation (""Rotation"", Float) = 0
        _FlipY    (""Flip Y"",   Float) = 1
        _FlipX    (""Flip X"",   Float) = 0
    }
    SubShader
    {
        Tags { ""RenderType""=""Opaque"" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f    { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };
            sampler2D _MainTex;
            float _Rotation;
            float _FlipY;
            float _FlipX;
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            float2 RotateUV(float2 uv, float deg)
            {
                uv -= 0.5;
                float rad = deg * (3.14159265 / 180.0);
                float s = sin(rad), c = cos(rad);
                uv = float2(c * uv.x - s * uv.y, s * uv.x + c * uv.y);
                return uv + 0.5;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;
                if (_FlipX > 0.5) uv.x = 1.0 - uv.x;
                return tex2D(_MainTex, clamp(RotateUV(uv, _Rotation), 0.0, 1.0));
            }
            ENDCG
        }
    }
    Fallback ""Unlit/Texture""
}";
            try
            {
#if UNITY_EDITOR
                return UnityEditor.ShaderUtil.CreateShaderAsset(src, false);
#else
                Debug.LogWarning("[SegWorker] Compilación desde string solo disponible en Editor.");
                return null;
#endif
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SegWorker] ShaderUtil_CreateFromString falló: {ex.Message}");
                return null;
            }
        }

        private void InitializeWorker(BackendType backend)
        {
            try
            {
                _runtimeModel = ModelLoader.Load(_modelAsset);
                _worker       = new Worker(_runtimeModel, backend);
                _backendType  = backend;

                if (_runtimeModel.outputs.Count > 0)
                {
                    _outputName = _runtimeModel.outputs[0].name;
                    Debug.Log($"[SegWorker] Output detectado: '{_outputName}'");
                }

                _inputTensor = new Tensor<float>(
                    new TensorShape(1, IMAGE_SIZE, IMAGE_SIZE, 3));
                _tensorReady = true;

                IsReady = true;
                Debug.Log($"[SegWorker] ✅ Inicializado — backend={backend} " +
                          $"IMAGE_SIZE={IMAGE_SIZE} rotation={_rotationDegrees}° " +
                          $"flipY={_flipY} flipX={_flipX}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SegWorker] Error: {ex.Message}\n{ex.StackTrace}");
                IsReady = false;
            }
        }

        public void ScheduleInference(RenderTexture sourceRT)
        {
            if (!IsReady || IsBusy || !_tensorReady) return;
            IsBusy = true; _pollFailCount = 0;
            UploadToTensorAndSchedule(ApplyRotation(sourceRT));
        }

        public void ScheduleInference(Texture2D texture)
        {
            if (!IsReady || IsBusy || !_tensorReady) return;
            IsBusy = true; _pollFailCount = 0;
            UploadToTensorAndSchedule(ApplyRotation(texture));
        }

        private void UploadToTensorAndSchedule(Texture source)
        {
            var transform = new TextureTransform()
                .SetDimensions(IMAGE_SIZE, IMAGE_SIZE, 3)
                .SetTensorLayout(TensorLayout.NHWC);
            TextureConverter.ToTensor(source, _inputTensor, transform);
            _worker.Schedule(_inputTensor);
        }

        private Texture ApplyRotation(Texture src)
        {
            if (!_rotateMatReady || (_rotationDegrees == 0 && !_flipY && !_flipX))
                return src;
            _rotateMat.SetFloat("_Rotation", _rotationDegrees);
            _rotateMat.SetFloat("_FlipY", _flipY ? 1f : 0f);
            _rotateMat.SetFloat("_FlipX", _flipX ? 1f : 0f);
            Graphics.Blit(src, _rotatedRT, _rotateMat);
            return _rotatedRT;
        }

        public bool PollResult()
        {
            if (!IsBusy) return false;

            _pollFailCount++;
            if (_pollFailCount > MAX_POLL_FAILS)
            {
                Debug.LogError($"[SegWorker] ⚠️ Timeout. Reseteando.");
                IsBusy = false; _pollFailCount = 0;
                return false;
            }

            var outputName = _outputName ?? "Identity:0";

            try
            {
                if (_worker.PeekOutput(outputName) is Tensor<int> intTensor)
                {
                    var data = intTensor.DownloadToArray();
                    LogShapeOnce(data.Length, intTensor.shape.ToString());
                    ComputeStats(data);
                    FinalizeInference();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SegWorker] PeekOutput<int>: {ex.Message}");
            }

            try
            {
                if (_worker.PeekOutput(outputName) is Tensor<float> floatTensor)
                {
                    var floatData = floatTensor.DownloadToArray();
                    LogShapeOnce(floatData.Length, floatTensor.shape.ToString());
                    var intData = new int[floatData.Length];
                    for (int i = 0; i < floatData.Length; i++)
                        intData[i] = Mathf.Clamp(Mathf.RoundToInt(floatData[i]), 0, 3);
                    ComputeStats(intData);
                    FinalizeInference();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SegWorker] PeekOutput<float>: {ex.Message}");
            }

            return false;
        }

        private void FinalizeInference()
        {
            IsBusy = false; _pollFailCount = 0;
            OnInferenceComplete?.Invoke();
        }

        private void ComputeStats(int[] data)
        {
            int countObs = 0, countFloor = 0, countWall = 0, total = data.Length;
            for (int i = 0; i < total; i++)
            {
                int cls = data[i];
                MaskData[i] = cls >= 0 && cls < 4 ? cls : 0;
                if (cls == CLASS_OBSTACLE) countObs++;
                if (cls == CLASS_FLOOR)    countFloor++;
                if (cls == CLASS_WALL)     countWall++;          
            }
            ObstacleRatio = (float)countObs   / total;
            FloorRatio    = (float)countFloor / total;
            WallRatio     = (float)countWall  / total;  
        }

        private void LogShapeOnce(int length, string shape)
        {
            if (_debugShapeLogged) return;
            _debugShapeLogged = true;
            Debug.Log($"[SegWorker] 📐 Output: length={length} shape={shape} " +
                      $"(esperado={IMAGE_SIZE * IMAGE_SIZE})");
        }

        public void SetRotation(int degrees)
        {
            _rotationDegrees = degrees;
            Debug.Log($"[SegWorker] 🔄 Rotación → {degrees}°");
        }

        public void SetFlipY(bool flip)
        {
            _flipY = flip;
            if (_rotateMatReady) _rotateMat.SetFloat("_FlipY", flip ? 1f : 0f);
            Debug.Log($"[SegWorker] 🔄 FlipY → {flip}");
        }

        public void SetFlipX(bool flip)
        {
            _flipX = flip;
            if (_rotateMatReady) _rotateMat.SetFloat("_FlipX", flip ? 1f : 0f);
            Debug.Log($"[SegWorker] 🔄 FlipX → {flip}");
        }

        public void Dispose()
        {
            _inputTensor?.Dispose();
            _inputTensor = null;
            _worker?.Dispose();
            _rotatedRT?.Release();
            if (_rotateMat != null) UnityEngine.Object.Destroy(_rotateMat);
            IsReady = false;
        }
    }
}