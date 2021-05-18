using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System;
using System.Collections.Generic;
using UnityEngine.Rendering.PostProcessing;


public class Mirror : MonoBehaviour
{
    /// <summary>
    /// 抗锯齿质量
    /// </summary>
    public enum AntiAlias
    {
        X1 = 1,
        X2 = 2,
        X4 = 4,
        X8 = 8
    }

    /// <summary>
    /// 反射质量
    /// </summary>
    public enum RenderQuality
    {
        Default,
        High,
        Medium,
        Low,
        VeryLow
    }

    [Header("目标相机")]
    public Camera TargetCamera;
    public string ReflectionSample = "_ReflectionTex";

    /// <summary>
    /// 反射图的尺寸
    /// </summary>
    public int TextureSize = 256;

    /// <summary>
    /// 裁剪面的偏移
    /// </summary>
    [Header("裁剪面的偏移")]
    public float ClipPlaneOffset = 0.01f;

    [Header("Optimization & Culling")]
    [Tooltip("Reflection Quality")]
    public RenderQuality renderQuality = RenderQuality.Default;

    [Tooltip("Mirror mask")]
    public LayerMask m_ReflectLayers = -1;

    public bool enableSelfCullingDistance = true;


    [Tooltip("The normal transform(transform.up as normal)")]
    public Transform normalTrans;

    private RenderTexture[] m_ReflectionTexture = new RenderTexture[5];

    [HideInInspector] public float m_SqrMaxdistance = 2500f;
    [HideInInspector] public float m_maxDistance = 50f;

    [HideInInspector]
    public float[] layerCullingDistances = new float[32];

    /// <summary>
    /// shader中纹理集的id
    /// </summary>
    private int uniqueTextureID = -1;

    /// <summary>
    /// 纹理集，用于多个摄像机的反射
    /// </summary>
    Texture2DArray _texArr;

    /// <summary>
    /// 网格渲染器
    /// </summary>
    private Renderer _meshRender;

    /// <summary>
    /// 当前的反射相机
    /// </summary>
    private Camera _reflectionCamera;

    /// <summary>
    /// 渲染器下的所有网格
    /// </summary>
    /// <typeparam name="Material"></typeparam>
    /// <returns></returns>
    private List<Material> allMats = new List<Material>();

    /// <summary>
    /// 当前的反射纹理索引
    /// </summary>
    private int _currentTextureIndex = 0;


    [Tooltip("MSAA anti alias")]
    public AntiAlias MSAA = AntiAlias.X8;

    void Awake()
    {
        uniqueTextureID = Shader.PropertyToID(ReflectionSample);
        if (!normalTrans)
        {
            normalTrans = new GameObject("Normal Trans").transform;
            normalTrans.position = transform.position;
            normalTrans.rotation = transform.rotation;
            normalTrans.SetParent(transform);
        }
        _meshRender = GetComponent<Renderer>();
        if (!_meshRender || !_meshRender.sharedMaterial)
        {
            Destroy(this);
        }

        for (int i = 0, length = _meshRender.sharedMaterials.Length; i < length; ++i)
        {
            Material m = _meshRender.sharedMaterials[i];
            if (!allMats.Contains(m))
                allMats.Add(m);
        }

        // 创建反射纹理
        m_SqrMaxdistance = m_maxDistance * m_maxDistance;

        // 创建反射相机
        GameObject go = new GameObject("MirrorCam", typeof(Camera), typeof(FlareLayer), typeof(PostProcessLayer));
        //go.hideFlags = HideFlags.HideAndDontSave;
        _reflectionCamera = go.GetComponent<Camera>();
        PostProcessLayer postProcessLayer = go.GetComponent<PostProcessLayer>();
        postProcessLayer.volumeLayer = 1 << normalTrans.gameObject.layer;

        go.transform.SetParent(normalTrans);
        go.transform.localPosition = Vector3.zero;
        _reflectionCamera.enabled = false;
        // _reflectionCamera.targetTexture = m_ReflectionTexture;
        _reflectionCamera.cullingMask = ~(1 << 4) & m_ReflectLayers.value;
        _reflectionCamera.layerCullSpherical = enableSelfCullingDistance;

        if (!enableSelfCullingDistance)
        {
            for (int i = 0, length = layerCullingDistances.Length; i < length; ++i)
            {
                layerCullingDistances[i] = 0;
            }
        }
        else
        {
            _reflectionCamera.layerCullDistances = layerCullingDistances;
        }
        _reflectionCamera.useOcclusionCulling = false;       //Custom Projection Camera should not use occlusionCulling!
        // SetTexture(m_ReflectionTexture);

        switch (renderQuality)
        {
            case RenderQuality.Default:
                _reflectionCamera.farClipPlane = 100f;
                break;
            case RenderQuality.High:
                _reflectionCamera.farClipPlane = 500f;
                break;
            case RenderQuality.Medium:
                _reflectionCamera.farClipPlane = 100f;
                break;
            case RenderQuality.Low:
                _reflectionCamera.farClipPlane = 50f;
                break;
            case RenderQuality.VeryLow:
                _reflectionCamera.farClipPlane = 10f;
                break;
        }
    }

    void Update()
    {
        // 每帧重置索引
        _currentTextureIndex = 0;
    }

    void OnDestroy()
    {
        //DestroyImmediate(m_ReflectionTexture);
        DestroyImmediate(_reflectionCamera.gameObject);
    }

    /// <summary>
    /// 反射相机的投影矩阵
    /// </summary>
    Matrix4x4 _reflectionMatrix = Matrix4x4.identity;

    /// <summary>
    /// 反射相机世界矩阵
    /// </summary>
    Matrix4x4 _reflectionCameraWorldMatrix;

    /// <summary>
    /// 如果有N个camera，这里会执行N次，batches会翻倍
    /// </summary>
    public void OnWillRenderObject()
    {
        if (!_meshRender.enabled)
            return;

        Camera temp_current = Camera.current;

        if (m_ReflectionTexture[_currentTextureIndex] == null)//如果纹理没创建，先创建
        {
            m_ReflectionTexture[_currentTextureIndex] = new RenderTexture(TextureSize, TextureSize, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Default);
            m_ReflectionTexture[_currentTextureIndex].name = "ReflectionTex " + GetInstanceID();
            m_ReflectionTexture[_currentTextureIndex].isPowerOfTwo = true;
            m_ReflectionTexture[_currentTextureIndex].filterMode = FilterMode.Trilinear;
            m_ReflectionTexture[_currentTextureIndex].antiAliasing = (int)MSAA;
            m_ReflectionTexture[_currentTextureIndex].autoGenerateMips = false;
            m_ReflectionTexture[_currentTextureIndex].Create();
        }
        _reflectionCamera.targetTexture = m_ReflectionTexture[_currentTextureIndex];

        if (_texArr == null)
        {
            _texArr = new Texture2DArray(m_ReflectionTexture[_currentTextureIndex].width, m_ReflectionTexture[_currentTextureIndex].height, 5, TextureFormat.RGBAHalf, false, false);
            for (int i = 0, length = allMats.Count; i < length; ++i)
            {
                Material m = allMats[i];
                m.SetTexture(uniqueTextureID, _texArr);
            }
        }

        // 纹理拷贝到shader目标
        Graphics.CopyTexture(m_ReflectionTexture[_currentTextureIndex], 0, 0, _texArr, _currentTextureIndex, 0);

        for (int i = 0, length = allMats.Count; i < length; ++i)
        {
            Material m = allMats[i];
            m.SetFloat("_Index", _currentTextureIndex);
        }
        _currentTextureIndex += 1;

        _reflectionCamera.fieldOfView = temp_current.fieldOfView;
        _reflectionCamera.aspect = temp_current.aspect;

        if (Vector3.SqrMagnitude(normalTrans.position - temp_current.transform.position) > m_SqrMaxdistance)
        {
            return;
        }

        Vector3 localPos = normalTrans.worldToLocalMatrix.MultiplyPoint3x4(temp_current.transform.position);
        if (localPos.y < 0)
        {
            return;
        }

        _reflectionCamera.transform.eulerAngles = temp_current.transform.eulerAngles;
        Vector3 localEuler = _reflectionCamera.transform.localEulerAngles;
        localEuler.x *= -1;
        localEuler.z *= -1;
        localPos.y *= -1;
        _reflectionCamera.transform.localEulerAngles = localEuler;
        _reflectionCamera.transform.localPosition = localPos;

        float d = -Vector3.Dot(normalTrans.up, normalTrans.position) - ClipPlaneOffset;
        Vector4 temp_reflectionPlane = new Vector4(normalTrans.up.x, normalTrans.up.y, normalTrans.up.z, d);

        CalculateReflectionMatrix(ref _reflectionMatrix, ref temp_reflectionPlane);
        _reflectionCameraWorldMatrix = temp_current.worldToCameraMatrix * _reflectionMatrix;
        _reflectionCamera.worldToCameraMatrix = _reflectionCameraWorldMatrix;
        Vector4 clipPlane = CameraSpacePlane(_reflectionCameraWorldMatrix, normalTrans.position, normalTrans.up);
        _reflectionCamera.projectionMatrix = temp_current.CalculateObliqueMatrix(clipPlane);
        GL.invertCulling = true;

#if UNITY_EDITOR
        if (renderQuality == RenderQuality.VeryLow)
        {
            if (_reflectionCamera.renderingPath != RenderingPath.VertexLit)
                _reflectionCamera.renderingPath = RenderingPath.VertexLit;
        }
        else if (_reflectionCamera.renderingPath != temp_current.renderingPath)
        {
            _reflectionCamera.renderingPath = temp_current.renderingPath;
        }
#endif
        _reflectionCamera.Render();
        GL.invertCulling = false;
    }

    private Vector4 CameraSpacePlane(Matrix4x4 worldToCameraMatrix, Vector3 pos, Vector3 normal)
    {
        Vector3 offsetPos = pos + normal * ClipPlaneOffset;
        Vector3 cpos = worldToCameraMatrix.MultiplyPoint3x4(offsetPos);
        Vector3 cnormal = worldToCameraMatrix.MultiplyVector(normal).normalized;
        return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
    }

    private static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, ref Vector4 plane)
    {
        reflectionMat.m00 = (1F - 2F * plane[0] * plane[0]);
        reflectionMat.m01 = (-2F * plane[0] * plane[1]);
        reflectionMat.m02 = (-2F * plane[0] * plane[2]);
        reflectionMat.m03 = (-2F * plane[3] * plane[0]);

        reflectionMat.m10 = (-2F * plane[1] * plane[0]);
        reflectionMat.m11 = (1F - 2F * plane[1] * plane[1]);
        reflectionMat.m12 = (-2F * plane[1] * plane[2]);
        reflectionMat.m13 = (-2F * plane[3] * plane[1]);

        reflectionMat.m20 = (-2F * plane[2] * plane[0]);
        reflectionMat.m21 = (-2F * plane[2] * plane[1]);
        reflectionMat.m22 = (1F - 2F * plane[2] * plane[2]);
        reflectionMat.m23 = (-2F * plane[3] * plane[2]);
    }
}
