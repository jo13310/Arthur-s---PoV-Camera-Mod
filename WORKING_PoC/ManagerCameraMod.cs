using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime.Injection;

namespace ManagerCameraMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class ManagerCameraMod : BasePlugin
{
    internal new static ManualLogSource Log;

    public override void Load()
    {
        // Plugin startup logic
        Log = base.Log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        // IL2CPP: register our managed class before using AddComponent<T>()
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ManagerCameraBootstrap>();
        }
        catch (System.Exception ex)
        {
            return;
        }

        // Bootstrap a persistent script that will attach the main camera
        try
        {
            var go = new GameObject("ManagerCameraBootstrap");
            Object.DontDestroyOnLoad(go);
            var component = go.AddComponent<ManagerCameraBootstrap>();
            component.Init(Log);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Error creating ManagerCameraBootstrap GameObject/component: {ex}");
        }
    }
}

public class ManagerCameraBootstrap : MonoBehaviour
{
    private const string ManagerPath = "MatchPlaybackController/MatchComponents/Match3DBuilder/Manager(Clone)/ManagerController";
    private const string BallPath = "MatchPlaybackController/MatchComponents/Match3DBuilder/BallPrefab(Clone)";
    private const string GameScene = "MatchPlayback";
    
    public KeyCode activationKey = KeyCode.F1;
    public Vector3 cameraLocalOffset = new Vector3(0f, 6f, -10f);

    private ManualLogSource _logger;

    private Transform _manager;
    private Transform _ball;
    private Camera _lastMainCamera;
    private Camera _customCamera;
    private bool _customCameraActive;
    public bool copySettingsFromMain = true;
    private bool _originalMainEnabled = true;
    private string _lastSceneName = string.Empty;
    
    // Reflection caches for Unity Input System (if present)
    private System.Type _tKeyboard;
    private System.Type _tKeyEnum;
    private System.Type _tKeyControl;
    private System.Reflection.PropertyInfo _currentInput;
    private System.Reflection.PropertyInfo _currentInputItem;
    private System.Reflection.PropertyInfo _currentInputWasPressed;

    public void Init(ManualLogSource logger)
    {
        _logger = logger;
    }
    
    private void Update()
    {
        var activeScene = SceneManager.GetActiveScene();

        // TODO: Temp fix: Detect scene changes without using SceneManager.activeSceneChanged (IL2CPP issues)
        if (activeScene.IsValid())
        {
            var sceneName = activeScene.name;
            if (_lastSceneName != sceneName)
            {
                if (_customCameraActive) DisableCustomCamera(true);

                _lastSceneName = sceneName;
                _manager = null;
                _ball = null;
                _lastMainCamera = null;
            }
        }

        // Only operate in the MatchPlayback scene.
        // TODO: Test removing this as the scene is destroyed the camera will be too (remove DontDestroyOnLoad)
        if (!activeScene.IsValid() || activeScene.name != GameScene)
        {
            if (_customCameraActive)
                DisableCustomCamera(true);
            return;
        }

        if (NewInputWasPressedThisFrame(activationKey))
        {
            if (_customCameraActive)
                DisableCustomCamera(false);
            else
                EnableCustomCamera();
        }

        // While active, keep aiming at the ball; if anything disappears, auto-deactivate
        if (_customCameraActive)
        {
            if (_customCamera == null || _manager == null || _ball == null)
            {
                ManagerCameraMod.Log?.LogWarning("ManagerCameraBootstrap: Required reference lost; deactivating our camera.");
                DisableCustomCamera(false);
                return;
            }

            _customCamera.transform.LookAt(_ball);
        }
    }
    

    // TODO: Ugly reflection, ideally i would use Unity's InputSystem API directly', not sure why that's not working
    private bool TryInitInputSystem()
    {
        if (_tKeyboard != null && _tKeyEnum != null && _tKeyControl != null && _currentInput != null && _currentInputItem != null && _currentInputWasPressed != null)
            return true;

        try
        {
            _tKeyboard = System.Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _tKeyEnum = System.Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            _tKeyControl = System.Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");
            if (_tKeyboard == null || _tKeyEnum == null || _tKeyControl == null)
                return false;

            _currentInput = _tKeyboard.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            _currentInputItem = _tKeyboard.GetProperty("Item", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            _currentInputWasPressed = _tKeyControl.GetProperty("wasPressedThisFrame", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            return _currentInput != null && _currentInputItem != null && _currentInputWasPressed != null;
        }
        catch
        {
            return false;
        }
    }

    private bool NewInputWasPressedThisFrame(KeyCode keyCode)
    {
        if (!TryInitInputSystem()) return false;

        var keyboard = _currentInput.GetValue(null, null);
        if (keyboard == null) return false;

        // Map KeyCode to InputSystem.Key. Handle F1 explicitly
        object keyEnumValue = null;
        try
        {
            if (keyCode == KeyCode.F1)
            {
                keyEnumValue = System.Enum.Parse(_tKeyEnum, "F1");
            }
            else
            {
                keyEnumValue = System.Enum.Parse(_tKeyEnum, keyCode.ToString(), ignoreCase: true);
            }
        }
        catch
        {
            return false;
        }

        // Access indexer: Keyboard[key]
        var keyControl = _currentInputItem.GetValue(keyboard, new object[] { keyEnumValue });
        if (keyControl == null) return false;

        var value = _currentInputWasPressed.GetValue(keyControl, null);
        return value is bool b && b;
    }
    
    private void EnableCustomCamera()
    {
        try
        {
            _lastMainCamera = Camera.main;

            var managerGo = GameObject.Find(ManagerPath);
            _manager = managerGo != null ? managerGo.transform : null;

            var ballGo = GameObject.Find(BallPath);
            _ball = ballGo != null ? ballGo.transform : null;

            if (_manager == null)
            {
                ManagerCameraMod.Log?.LogWarning($"ManagerCameraBootstrap: Activate failed — Manager not found at path '{ManagerPath}'.");
                return;
            }
            if (_ball == null)
            {
                ManagerCameraMod.Log?.LogWarning($"ManagerCameraBootstrap: Activate failed — Ball not found at path '{BallPath}'.");
                return;
            }

            // Create camera
            if (_customCamera == null)
            {
                var go = new GameObject("ManagerCam_Custom");
                _customCamera = go.AddComponent<Camera>();
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                // TODO: Check if really needed
                go.tag = "MainCamera";

                // Copy visual settings from the game's main camera
                // TODO: more wonky stuff could be done here
                if (copySettingsFromMain && _lastMainCamera != null)
                {
                    try
                    {
                        _customCamera.fieldOfView = _lastMainCamera.fieldOfView;
                        _customCamera.nearClipPlane = _lastMainCamera.nearClipPlane;
                        _customCamera.farClipPlane = _lastMainCamera.farClipPlane;
                        _customCamera.allowHDR = _lastMainCamera.allowHDR;
                        _customCamera.allowMSAA = _lastMainCamera.allowMSAA;
                        _customCamera.clearFlags = _lastMainCamera.clearFlags;
                        _customCamera.backgroundColor = _lastMainCamera.backgroundColor;
                        _customCamera.cullingMask = _lastMainCamera.cullingMask;
                        _customCamera.depth = _lastMainCamera.depth + 1f; // render on top just in case
                    }
                    catch
                    {
                        /* ignore per-field copy issues on IL2CPP */ 
                    }
                }
            }

            // Parent and orient camera relative to the Manager and aim at the ball
            _customCamera.transform.SetParent(_manager, worldPositionStays: false);
            _customCamera.transform.localPosition = cameraLocalOffset;
            _customCamera.transform.localRotation = Quaternion.identity;
            _customCamera.transform.LookAt(_ball);

            // Enable camera and disable the game's camera to avoid double rendering
            if (_lastMainCamera != null)
            {
                _originalMainEnabled = _lastMainCamera.enabled;
                _lastMainCamera.enabled = false;
            }
            _customCamera.gameObject.SetActive(true);
            _customCamera.enabled = true;
            _customCameraActive = true;
            ManagerCameraMod.Log?.LogInfo("ManagerCameraBootstrap: Custom camera activated (tracking Ball). Press key again to restore.");
        }
        catch (System.Exception ex)
        {
            ManagerCameraMod.Log?.LogError($"ManagerCameraBootstrap: ActivateOurCamera failed: {ex}");
            _customCameraActive = false;
        }
    }

    private void DisableCustomCamera(bool dueToSceneChange)
    {
        try
        {
            // Disable camera but keep it around for reuse
            if (_customCamera != null)
            {
                _customCamera.enabled = false;
                _customCamera.gameObject.SetActive(false);
                // Detach from manager to avoid being destroyed if manager is destroyed
                _customCamera.transform.SetParent(null, worldPositionStays: true);
            }

            // Re-enable the game's camera
            if (_lastMainCamera != null)
            {
                try
                {
                    _lastMainCamera.enabled = _originalMainEnabled;
                }
                catch { }
            }

            _customCameraActive = false;
            if (dueToSceneChange)
                ManagerCameraMod.Log?.LogInfo("ManagerCameraBootstrap: Scene change detected — custom camera deactivated.");
            else
                ManagerCameraMod.Log?.LogInfo("ManagerCameraBootstrap: Custom camera deactivated; restored game camera.");
        }
        catch (System.Exception ex)
        {
            ManagerCameraMod.Log?.LogError($"ManagerCameraBootstrap: DeactivateOurCamera failed: {ex}");
        }
        finally
        {
            _manager = null;
            _ball = null;
            _lastMainCamera = null;
        }
    }
}
