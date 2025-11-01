using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace ArthurRayPovMod;

public class PIDController
{
    private float _proportionalGain;
    private float _integralGain;
    private float _derivativeGain;
    private float _integral;
    private float _previousError;
    private float _previousTime;

    public PIDController(float proportionalGain, float integralGain, float derivativeGain)
    {
        _proportionalGain = proportionalGain;
        _integralGain = integralGain;
        _derivativeGain = derivativeGain;
        Reset();
    }

    public float Update(float currentError, float deltaTime, float time = -1f)
    {
        if (time < 0f)
            time = Time.unscaledTime;

        var dt = time - _previousTime;
        if (dt <= 0f)
            return 0f;

        // Proportional term
        var proportional = _proportionalGain * currentError;

        // Integral term
        _integral += currentError * dt;
        var integral = _integralGain * _integral;

        // Derivative term
        var derivative = _derivativeGain * ((currentError - _previousError) / dt);

        // Calculate output
        var output = proportional + integral + derivative;

        // Store values for next iteration
        _previousError = currentError;
        _previousTime = time;

        // Clamp output to prevent oscillation
        return Mathf.Clamp(output, -1f, 1f);
    }

    public void Reset()
    {
        _integral = 0f;
        _previousError = 0f;
        _previousTime = Time.unscaledTime;
    }
}

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class ArthurRayPovMod : BasePlugin
{
    internal new static ManualLogSource Log;

    public override void Load()
    {
        // Plugin startup logic
        Log = base.Log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        Log.LogInfo("Credits: GerKo & Brululul");

        // IL2CPP: register our managed class before using AddComponent<T>()
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ArthurRayPovBootstrap>();
        }
        catch (System.Exception)
        {
            return;
        }

        // Bootstrap a persistent script that will attach the main camera
        try
        {
            var go = new GameObject("ArthurRayPovBootstrap");
            Object.DontDestroyOnLoad(go);
            var component = go.AddComponent<ArthurRayPovBootstrap>();
            component.Init(Log);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Error creating ArthurRayPovBootstrap GameObject/component: {ex}");
        }
    }
}

public class ArthurRayPovBootstrap : MonoBehaviour
{
    private const string MatchBuilderRoot = "MatchPlaybackController/MatchComponents/Match3DBuilder";
    private static readonly string[] BallObjectPaths =
    {
        "MatchPlaybackController/MatchComponents/Match3DBuilder/BallPrefab(Clone)",
        "MatchPlaybackController/MatchComponents/Match3DBuilder/BallPrefab"
    };

    private static readonly string[] BallObjectNames =
    {
        "BallPrefab(Clone)",
        "BallPrefab"
    };
    private const string GameScene = "MatchPlayback";
    private const float PlayerPovForwardOffset = 4.6f;
    private const float PlayerPovVerticalOffset = 0.7f;  // Lowered for better POV
    private const float PlayerPovLookHeightOffset = 1.5f;  // Lowered for better POV
    private const float ManagerPovForwardOffset = -0.12f;
    private const float ManagerPovVerticalOffset = 0.05f;
    private const float ManagerSidelineOffset = 18f;
    private const float ManagerSidelineBackOffset = 4f;
    private const float ManagerLookHeightOffset = 0.15f;
    private const float ManagerSidelineHeight = 2.2f;
    private const float ManagerBallFollowSmoothTime = 0.35f;
    
    public KeyCode normalCameraKey = KeyCode.F1;
    public KeyCode ballCarrierPovKey = KeyCode.F2;
    public KeyCode previousPlayerKey = KeyCode.F3;
    public KeyCode nextPlayerKey = KeyCode.F4;
    public KeyCode managerPovKey = KeyCode.F5;
    public KeyCode vrPovKey = KeyCode.F6;
    public float vrInterPupillaryDistance = 0.064f;
    public float vrFieldOfView = 96f;
    public bool vrAutoFollowBallCarrier = true;
    public bool copySettingsFromMain = true;

    private ManualLogSource _logger;

    // PID Controllers for smooth camera movement
    private PIDController _positionPIDX;
    private PIDController _positionPIDY;
    private PIDController _positionPIDZ;
    private PIDController _rotationPIDX;
    private PIDController _rotationPIDY;
    private PIDController _rotationPIDZ;

    // Smooth movement parameters
    public float positionSmoothingStrength = 2.0f;
    public float rotationSmoothingStrength = 3.0f;
    public float playerTransitionDuration = 0.8f;
    
    // POV persistence
    private bool _isPovModeActive = false;
    private bool _vrFreeLookEnabled = true;
    
    // Smooth transitions
    private Vector3 _currentCameraVelocity;
    private float _currentPlayerTransitionTime;
    private Avatar _transitionTargetPlayer;
    
    private Transform _ball;
    private Camera _lastMainCamera;
    private Camera _customCamera;
    private bool _customCameraActive;
    private bool _originalMainEnabled = true;
    private string _originalMainTag = "MainCamera";
    private string _lastSceneName = string.Empty;
    private Transform _matchBuilder;
    private CameraMode _cameraMode = CameraMode.None;
    private bool _autoFollowBallCarrier;
    private VrRig _vrRig;
    private bool _vrRigActive;
    private readonly List<Avatar> _players = new();
    private readonly List<Avatar> _managers = new();
    private Avatar _currentPlayer;
    private Avatar _currentManager;
    private int _currentPlayerIndex = -1;
    private int _currentManagerIndex = -1;
    private float _nextPlayerScanTime;
    private float _nextManagerScanTime;
    private GUIStyle _hudStyle;
    private bool _loggedMissingPlayerOnce;
    private bool _loggedMissingManagerOnce;
    private bool _legacyInputUnavailable;
    private bool _lastMatch3DActive;
    private bool _hasSeenBall;
    private bool _hasSmoothedBall;
    private Vector3 _smoothedBallPosition;
    private float _ballLastActiveTime;
    private const float BallStateGraceSeconds = 0.5f;
    private bool _restoreOnNext3D;
    private CustomCameraState _pendingRestoreState;
    
    // Reflection caches for Unity Input System (if present)
    private System.Type _tKeyboard;
    private System.Type _tKeyEnum;
    private System.Type _tKeyControl;
    private System.Reflection.PropertyInfo _currentInput;
    private System.Reflection.PropertyInfo _currentInputItem;
    private System.Reflection.PropertyInfo _currentInputWasPressed;

    private enum CameraMode
    {
        None = 0,
        PlayerPov = 1,
        ManagerPov = 2,
        VrPov = 3
    }

    private struct CustomCameraState
    {
        public CameraMode Mode;
        public bool AutoFollowBallCarrier;
        public int PlayerInstanceId;
        public int ManagerInstanceId;
        public bool HasState;
    }

    private class Avatar
    {
        public Transform Root;
        public Transform Head;
        public string DisplayName;
        public int InstanceId;

        public bool IsValid => Root != null;
    }

    public void Init(ManualLogSource logger)
    {
        _logger = logger;
        
        // Initialize PID controllers
        _positionPIDX = new PIDController(positionSmoothingStrength, 0f, 0f);
        _positionPIDY = new PIDController(positionSmoothingStrength, 0f, 0f);
        _positionPIDZ = new PIDController(positionSmoothingStrength, 0f, 0f);
        _rotationPIDX = new PIDController(rotationSmoothingStrength, 0f, 0f);
        _rotationPIDY = new PIDController(rotationSmoothingStrength, 0f, 0f);
        _rotationPIDZ = new PIDController(rotationSmoothingStrength, 0f, 0f);
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
                if (_customCameraActive)
                {
                    ClearPendingRestore();
                    DisableCustomCamera(true);
                }

                _lastSceneName = sceneName;
                _ball = null;
                _lastMainCamera = null;
                _matchBuilder = null;
                _players.Clear();
                _currentPlayer = null;
                _currentPlayerIndex = -1;
                _cameraMode = CameraMode.None;
                _lastMatch3DActive = false;
                _hasSeenBall = false;
                _hasSmoothedBall = false;
                _smoothedBallPosition = Vector3.zero;
                _ballLastActiveTime = 0f;
            }
        }

        // Only operate in the MatchPlayback scene.
        // TODO: Test removing this as the scene is destroyed the camera will be too (remove DontDestroyOnLoad)
        if (!activeScene.IsValid() || activeScene.name != GameScene)
        {
            if (_customCameraActive)
            {
                ClearPendingRestore();
                DisableCustomCamera(true);
            }
            _lastMatch3DActive = false;
            _hasSeenBall = false;
            _hasSmoothedBall = false;
            _smoothedBallPosition = Vector3.zero;
            _ballLastActiveTime = 0f;
            return;
        }

        var match3DActive = IsMatch3DViewActive();
        if (!match3DActive && _lastMatch3DActive && _customCameraActive)
        {
            RememberCurrentCustomState();
            _restoreOnNext3D = _pendingRestoreState.HasState;
            if (_restoreOnNext3D)
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Match UI switched to 2D; saving POV for auto-restore.");
            else
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Match UI switched to 2D; returning to default camera.");

            DisableCustomCamera(false);
        }
        else if (match3DActive && !_lastMatch3DActive)
        {
            if (_restoreOnNext3D && _pendingRestoreState.HasState)
            {
                RestorePendingCustomState();
            }
            else
            {
                _restoreOnNext3D = false;
                _pendingRestoreState = default;
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Match UI switched back to 3D; staying on default camera.");
            }
        }
        _lastMatch3DActive = match3DActive;

        if (NewInputWasPressedThisFrame(normalCameraKey))
        {
            ClearPendingRestore();
            if (_customCameraActive) DisableCustomCamera(false);
        }

        if (NewInputWasPressedThisFrame(ballCarrierPovKey))
        {
            // Toggle POV persistence
            if (_isPovModeActive && _cameraMode == CameraMode.PlayerPov)
            {
                _isPovModeActive = false;
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: POV persistence disabled.");
                return;
            }
            
            ActivatePlayerPov(autoFollowBallCarrier: true);
        }

        if (NewInputWasPressedThisFrame(managerPovKey))
        {
            ActivateManagerPov();
        }

        if (NewInputWasPressedThisFrame(previousPlayerKey))
        {
            if (_cameraMode == CameraMode.ManagerPov)
                CycleManager(-1);
            else
                CyclePlayer(-1);
        }

        if (NewInputWasPressedThisFrame(nextPlayerKey))
        {
            if (_cameraMode == CameraMode.ManagerPov)
                CycleManager(1);
            else
                CyclePlayer(1);
        }

        if (NewInputWasPressedThisFrame(vrPovKey))
        {
            ActivateVrPov();
        }

        // Handle VR free look
        if (_cameraMode == CameraMode.VrPov && _vrFreeLookEnabled)
        {
            float mouseLookX = 0f;
            float mouseLookY = 0f;
            
            try
            {
                if (UnityEngine.Input.GetMouseButton(0)) // Right mouse button
                mouseLookX = UnityEngine.Input.GetAxis("Mouse X");
                mouseLookY = UnityEngine.Input.GetAxis("Mouse Y");
            }
            catch
            {
                // Fallback if mouse input not available
            var horizontal = UnityEngine.Input.GetAxis("Horizontal");
                var vertical = UnityEngine.Input.GetAxis("Vertical");
                mouseLookX = horizontal * 2f;
                mouseLookY = vertical * 2f;
            }
            
            // Apply look to VR rig
            if (_vrRig != null && _vrRigActive && _currentPlayer != null && _currentPlayer.IsValid)
            {
                var currentRotation = _vrRig._headTransform.rotation;
                var mouseRotation = Quaternion.Euler(mouseLookY * -2f, mouseLookX * 2f, 0f);
                _vrRig._headTransform.rotation = currentRotation * mouseRotation;
            }
        }

        // While active, keep aiming at the ball; if anything disappears, auto-deactivate
        if (_customCameraActive)
        {
            switch (_cameraMode)
            {
                case CameraMode.PlayerPov:
                    UpdatePlayerPovCamera();
                    break;
                case CameraMode.ManagerPov:
                    UpdateManagerPovCamera();
                    break;
                case CameraMode.VrPov:
                    UpdateVrPovCamera();
                    break;
            }
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
        if (TryGetInputSystemKeyDown(keyCode, out var pressed))
            return pressed;

        if (_legacyInputUnavailable)
            return false;

        try
        {
            return UnityEngine.Input.GetKeyDown(keyCode);
        }
        catch (Exception ex)
        {
            _legacyInputUnavailable = true;
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Legacy Input.GetKeyDown unavailable ({ex.Message}); relying on new Input System only.");
            return false;
        }
    }

    private bool TryGetInputSystemKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;

        if (!TryInitInputSystem()) return false;

        var keyboard = _currentInput.GetValue(null, null);
        if (keyboard == null) return false;

        // Map KeyCode to InputSystem.Key
        object keyEnumValue = null;
        try
        {
            keyEnumValue = System.Enum.Parse(_tKeyEnum, keyCode.ToString(), ignoreCase: true);
        }
        catch
        {
            return false;
        }

        // Access indexer: Keyboard[key]
        var keyControl = _currentInputItem.GetValue(keyboard, new object[] { keyEnumValue });
        if (keyControl == null) return false;

        var value = _currentInputWasPressed.GetValue(keyControl, null);
        pressed = value is bool b && b;
        return true;
    }

    private void ActivatePlayerPov(bool autoFollowBallCarrier)
    {
        if (!EnableCustomCamera())
            return;

        var stateChanged = _cameraMode != CameraMode.PlayerPov || !_customCameraActive || _autoFollowBallCarrier != autoFollowBallCarrier;

        _cameraMode = CameraMode.PlayerPov;
        _isPovModeActive = true;  // Enable POV persistence
        _autoFollowBallCarrier = autoFollowBallCarrier;
        _currentManager = null;
        _currentManagerIndex = -1;

        if (autoFollowBallCarrier)
        {
            _currentPlayer = null;
            _currentPlayerIndex = -1;
        }

        EnsurePlayerList(forceRefresh: autoFollowBallCarrier);

        if (!_autoFollowBallCarrier && _players.Count > 0)
        {
            if (_currentPlayerIndex < 0 || _currentPlayerIndex >= _players.Count)
                _currentPlayerIndex = 0;

            _currentPlayer = _players[_currentPlayerIndex];
        }

        // Reset PID controllers when switching players
        ResetPIDs();

        if (stateChanged)
        {
            var modeLabel = autoFollowBallCarrier ? "ball-carrier auto follow" : "manual selection";
            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Player POV camera active ({modeLabel}).");
        }
    }

    private void ActivateManagerPov()
    {
        if (!EnableCustomCamera())
            return;

        var stateChanged = _cameraMode != CameraMode.ManagerPov || !_customCameraActive;

        _cameraMode = CameraMode.ManagerPov;
        _autoFollowBallCarrier = false;
        _currentPlayer = null;
        _currentPlayerIndex = -1;

        EnsureManagerList(forceRefresh: true);

        if (_managers.Count == 0)
        {
            ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: No managers found for POV camera.");
            _currentManager = null;
            _currentManagerIndex = -1;
            return;
        }

        if (_currentManagerIndex < 0 || _currentManagerIndex >= _managers.Count)
            _currentManagerIndex = 0;

        _currentManager = _managers[_currentManagerIndex];
        _loggedMissingManagerOnce = false;

        if (stateChanged && _currentManager != null)
        {
            var label = string.IsNullOrEmpty(_currentManager.DisplayName) ? "Manager" : _currentManager.DisplayName;
            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Manager POV camera active ({label}).");
        }
    }

    private void CycleManager(int direction)
    {
        if (direction == 0)
            return;

        EnsureManagerList(forceRefresh: _managers.Count == 0);

        if (_managers.Count == 0)
        {
            ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: No managers found to cycle POV camera.");
            return;
        }

        if (!_customCameraActive || _cameraMode != CameraMode.ManagerPov)
        {
            ActivateManagerPov();
        }

        if (_managers.Count == 0 || _currentManager == null)
            return;

        if (_currentManagerIndex < 0 || _currentManagerIndex >= _managers.Count)
        {
            _currentManagerIndex = direction > 0 ? 0 : _managers.Count - 1;
        }
        else
        {
            _currentManagerIndex = (_currentManagerIndex + direction) % _managers.Count;
            if (_currentManagerIndex < 0)
                _currentManagerIndex += _managers.Count;
        }

        _currentManager = _managers[_currentManagerIndex];
        _loggedMissingManagerOnce = false;

        var label = string.IsNullOrEmpty(_currentManager.DisplayName) ? "Manager" : _currentManager.DisplayName;
        ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Manager POV camera active ({label}).");
    }

    private void CyclePlayer(int direction)
    {
        if (direction == 0)
            return;

        EnsurePlayerList(forceRefresh: _players.Count == 0);

        if (_players.Count == 0)
        {
            ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: No players found to cycle POV camera.");
            return;
        }

        if (!_customCameraActive || _cameraMode != CameraMode.PlayerPov || _autoFollowBallCarrier)
        {
            ActivatePlayerPov(autoFollowBallCarrier: false);
        }

        if (_players.Count == 0)
            return;

        var targetIndex = _currentPlayerIndex;
        if (targetIndex < 0 || targetIndex >= _players.Count)
        {
            targetIndex = direction > 0 ? 0 : _players.Count - 1;
        }
        else
        {
            targetIndex = (targetIndex + direction) % _players.Count;
            if (targetIndex < 0)
                targetIndex += _players.Count;
        }

        // Start smooth transition
        _transitionTargetPlayer = _players[targetIndex];
        _currentPlayerTransitionTime = 0f;
        _autoFollowBallCarrier = false;
        
        ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: POV transitioning to {_transitionTargetPlayer.DisplayName}.");
    }

    private void ActivateVrPov()
    {
        if (!EnableCustomCamera())
            return;

        _cameraMode = CameraMode.VrPov;
        _autoFollowBallCarrier = vrAutoFollowBallCarrier;
        _currentManager = null;
        _currentManagerIndex = -1;
        _currentPlayer = null;
        _currentPlayerIndex = -1;

        if (_vrRig == null)
        {
            _vrRig = new VrRig();
            _vrRig.Initialize(_customCamera);
            _vrRig.SetInterpupillaryDistance(vrInterPupillaryDistance);
            _vrRig.SetFieldOfView(vrFieldOfView);
        }

        if (!_vrRigActive)
        {
            _vrRig.SetActive(true);
            _vrRigActive = true;
        }

        ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: VR POV camera active.");
    }

    private void UpdateVrPovCamera()
    {
        if (!_customCameraActive || _customCamera == null || _vrRig == null || !_vrRigActive)
            return;

        EnsureBallReference();
        EnsurePlayerList();

        if (vrAutoFollowBallCarrier)
        {
            var carrier = FindClosestPlayerToBall();
            if (carrier != null)
            {
                SetCurrentPlayer(carrier);
            }
        }

        Vector3 vrTargetPosition;
        Quaternion vrTargetRotation;

        if (_currentPlayer != null && _currentPlayer.IsValid)
        {
            var headPosition = GetAvatarHeadPosition(_currentPlayer);
            var forward = _currentPlayer.Root != null ? _currentPlayer.Root.forward : Vector3.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            else
                forward.Normalize();

            vrTargetPosition = headPosition - forward * PlayerPovForwardOffset + Vector3.up * PlayerPovVerticalOffset;

            Vector3 vrLookTarget;
            if (_ball != null)
            {
                vrLookTarget = _ball.position + Vector3.up * PlayerPovLookHeightOffset;
            }
            else
            {
                vrLookTarget = headPosition + forward * 7.5f + Vector3.up * (PlayerPovLookHeightOffset + 0.6f);
            }

            var vrLookDirection = vrLookTarget - headPosition;
            if (vrLookDirection.sqrMagnitude < 0.0001f)
            {
                vrLookDirection = forward + Vector3.up * PlayerPovLookHeightOffset;
            }

            vrTargetRotation = Quaternion.LookRotation(vrLookDirection.normalized, Vector3.up);
        }
        else
        {
            if (_ball != null)
            {
                vrTargetPosition = _ball.position + Vector3.up * 1.6f;
                vrTargetRotation = Quaternion.LookRotation(_ball.position - vrTargetPosition, Vector3.up);
            }
            else
            {
                vrTargetPosition = _customCamera.transform.position;
                vrTargetRotation = _customCamera.transform.rotation;
            }
        }

        _vrRig.UpdateRig(vrTargetPosition, vrTargetRotation);
    }

    private void UpdatePlayerPovCamera()
    {
        if (!_customCameraActive || _customCamera == null)
            return;

        EnsureBallReference();
        EnsurePlayerList();

        // Handle smooth player transitions
        if (_transitionTargetPlayer != null && _transitionTargetPlayer.IsValid)
        {
            _currentPlayerTransitionTime += Time.unscaledDeltaTime;
            var transitionProgress = Mathf.Clamp01(_currentPlayerTransitionTime / playerTransitionDuration);
            
            if (transitionProgress >= 1.0f)
            {
                // Transition complete
                _currentPlayer = _transitionTargetPlayer;
                _currentPlayerIndex = _players.FindIndex(p => p.InstanceId == _transitionTargetPlayer.InstanceId);
                _transitionTargetPlayer = null;
                _currentPlayerTransitionTime = 0f;
                ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: POV switched to {_currentPlayer.DisplayName}.");
            }
            else
            {
                // Continue transition
                var startPos = GetAvatarHeadPosition(_currentPlayer) ?? _customCamera.transform.position;
                var endPos = GetAvatarHeadPosition(_transitionTargetPlayer);
                
                var endForward = _transitionTargetPlayer.Root != null ? _transitionTargetPlayer.Root.forward : Vector3.forward;
                endForward.y = 0f;
                if (endForward.sqrMagnitude < 0.0001f)
                    endForward = Vector3.forward;
                else
                    endForward.Normalize();

                var lerpedPosition = Vector3.Lerp(startPos, endPos, transitionProgress);
                var targetPos = lerpedPosition - endForward * PlayerPovForwardOffset + Vector3.up * PlayerPovVerticalOffset;

                Vector3 transitionLookTarget;
                if (_ball != null)
                {
                    transitionLookTarget = _ball.position + Vector3.up * PlayerPovLookHeightOffset;
                }
                else
                {
                    transitionLookTarget = lerpedPosition + endForward * 7.5f + Vector3.up * (PlayerPovLookHeightOffset + 0.6f);
                }

                var lookDirection = transitionLookTarget - lerpedPosition;
                if (lookDirection.sqrMagnitude < 0.0001f)
                {
                    lookDirection = endForward + Vector3.up * PlayerPovLookHeightOffset;
                }

                var targetRotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
                SmoothUpdateCamera(targetPos, targetRotation);
                return;
            }
        }

        if (_autoFollowBallCarrier)
        {
            var carrier = FindClosestPlayerToBall();
            if (carrier != null)
            {
                SetCurrentPlayer(carrier);
            }
        }

        if (_currentPlayer == null || !_currentPlayer.IsValid)
        {
            if (!_loggedMissingPlayerOnce)
            {
                ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: POV target missing; awaiting next valid player.");
                _loggedMissingPlayerOnce = true;
            }

            if (_ball != null)
            {
                var fallbackPosition = _ball.position + Vector3.up * 1.6f;
                SmoothUpdateCamera(fallbackPosition, Quaternion.LookRotation(_ball.position - fallbackPosition, Vector3.up));
            }
            return;
        }

        _loggedMissingPlayerOnce = false;

        var headPosition = GetAvatarHeadPosition(_currentPlayer);
        var forward = _currentPlayer.Root != null ? _currentPlayer.Root.forward : Vector3.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        else
            forward.Normalize();

        var targetPosition = headPosition - forward * PlayerPovForwardOffset + Vector3.up * PlayerPovVerticalOffset;

        Vector3 lookTarget;
        if (_ball != null)
        {
            lookTarget = _ball.position + Vector3.up * PlayerPovLookHeightOffset;
        }
        else
        {
            lookTarget = headPosition + forward * 7.5f + Vector3.up * (PlayerPovLookHeightOffset + 0.6f);
        }

        var lookDirection = lookTarget - headPosition;
        if (lookDirection.sqrMagnitude < 0.0001f)
        {
            lookDirection = forward + Vector3.up * PlayerPovLookHeightOffset;
        }

        var targetRotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
        SmoothUpdateCamera(targetPosition, targetRotation);
    }

    private void ResetPIDs()
    {
        _positionPIDX?.Reset();
        _positionPIDY?.Reset();
        _positionPIDZ?.Reset();
        _rotationPIDX?.Reset();
        _rotationPIDY?.Reset();
        _rotationPIDZ?.Reset();
    }

    private void SmoothUpdateCamera(Vector3 targetPosition, Quaternion targetRotation)
    {
        var deltaTime = Time.unscaledDeltaTime;
        
        // Smooth position using PID controllers
        var positionErrorX = targetPosition.x - _customCamera.transform.position.x;
        var positionErrorY = targetPosition.y - _customCamera.transform.position.y;
        var positionErrorZ = targetPosition.z - _customCamera.transform.position.z;
        
        var smoothX = _positionPIDX.Update(positionErrorX, deltaTime);
        var smoothY = _positionPIDY.Update(positionErrorY, deltaTime);
        var smoothZ = _positionPIDZ.Update(positionErrorZ, deltaTime);
        
        var smoothPosition = _customCamera.transform.position + new Vector3(smoothX, smoothY, smoothZ) * positionSmoothingStrength;
        
        // Smooth rotation using PID controllers
        var currentEuler = _customCamera.transform.rotation.eulerAngles;
        var targetEuler = targetRotation.eulerAngles;
        
        // Handle angle wrapping
        var rotErrorX = Mathf.DeltaAngle(currentEuler.x, targetEuler.x);
        var rotErrorY = Mathf.DeltaAngle(currentEuler.y, targetEuler.y);
        var rotErrorZ = Mathf.DeltaAngle(currentEuler.z, targetEuler.z);
        
        var smoothRotX = _rotationPIDX.Update(rotErrorX, deltaTime);
        var smoothRotY = _rotationPIDY.Update(rotErrorY, deltaTime);
        var smoothRotZ = _rotationPIDZ.Update(rotErrorZ, deltaTime);
        
        var smoothEuler = currentEuler + new Vector3(smoothRotX, smoothRotY, smoothRotZ) * rotationSmoothingStrength;
        var smoothRotation = Quaternion.Euler(smoothEuler);
        
        _customCamera.transform.position = smoothPosition;
        _customCamera.transform.rotation = smoothRotation;
    }

    private void UpdateManagerPovCamera()
    {
        if (!_customCameraActive || _customCamera == null)
            return;

        EnsureBallReference();
        EnsureManagerList();

        var fallbackFocus = _customCamera.transform.position + _customCamera.transform.forward * 5f;
        var smoothedBallGround = GetStableBallGroundPosition(fallbackFocus);

        var hasAnchor = false;

        if (_managers.Count > 0)
        {
            if (_currentManager == null || !_currentManager.IsValid || _currentManagerIndex < 0 || _currentManagerIndex >= _managers.Count)
            {
                _currentManager = _managers[0];
                _currentManagerIndex = 0;
            }

            if (_currentManager != null && _currentManager.IsValid)
            {
                var headPosition = GetAvatarHeadPosition(_currentManager);
                if (headPosition != Vector3.zero)
                {
                    _loggedMissingManagerOnce = false;

                    var forward = _currentManager.Root != null ? _currentManager.Root.forward : Vector3.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.0001f)
                        forward = Vector3.forward;
                    else
                        forward.Normalize();

                    var cameraPosition = headPosition - forward * ManagerPovForwardOffset + Vector3.up * ManagerPovVerticalOffset;
                    _customCamera.transform.position = cameraPosition;

                    Vector3 lookTarget;
                    if (_ball != null || _hasSmoothedBall)
                        lookTarget = smoothedBallGround + Vector3.up * ManagerLookHeightOffset;
                    else
                        lookTarget = headPosition + forward * 5f;

                    var lookDirection = lookTarget - headPosition;
                    if (lookDirection.sqrMagnitude < 0.0001f)
                    {
                        lookDirection = forward;
                    }

                    _customCamera.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
                    hasAnchor = true;
                }
                else if (!_loggedMissingManagerOnce)
                {
                    ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: Manager POV target missing head transform.");
                    _loggedMissingManagerOnce = true;
                }
            }
        }
        else
        {
            _currentManager = null;
            _currentManagerIndex = -1;
        }

        if (hasAnchor)
            return;

        _loggedMissingManagerOnce = false;

        var ballPosition = smoothedBallGround;

        Vector3 fieldForward = Vector3.forward;
        Vector3 fieldRight = Vector3.right;
        if (_matchBuilder != null)
        {
            fieldForward = _matchBuilder.forward;
            fieldRight = _matchBuilder.right;
        }

        fieldForward.y = 0f;
        fieldRight.y = 0f;

        if (fieldForward.sqrMagnitude < 0.0001f)
            fieldForward = Vector3.forward;
        else
            fieldForward.Normalize();

        if (fieldRight.sqrMagnitude < 0.0001f)
            fieldRight = Vector3.right;
        else
            fieldRight.Normalize();

        const float sidelineSign = -1f;
        var lateralOffset = fieldRight * ManagerSidelineOffset * sidelineSign;
        var backwardOffset = fieldForward * ManagerSidelineBackOffset;
        var cameraPos = ballPosition + lateralOffset - backwardOffset + Vector3.up * ManagerSidelineHeight;

        _customCamera.transform.position = cameraPos;

        var lookTargetFallback = ballPosition + Vector3.up * ManagerLookHeightOffset;
        var lookDir = lookTargetFallback - cameraPos;
        if (lookDir.sqrMagnitude < 0.0001f)
            lookDir = fieldForward;

        _customCamera.transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
    }

    private bool IsMatch3DViewActive()
    {
        EnsureBallReference();
        var now = Time.unscaledTime;
        var hadBallReference = _ball != null;

        if (_ball != null)
        {
            var ballObject = _ball.gameObject;
            if (ballObject == null)
            {
                _ball = null;
            }
            else
            {
                var isActive = ballObject.activeSelf && ballObject.activeInHierarchy;
                if (isActive)
                {
                    _ballLastActiveTime = now;
                    return true;
                }

                if (_hasSeenBall)
                {
                    if (now - _ballLastActiveTime <= BallStateGraceSeconds)
                        return _lastMatch3DActive;
                    return false;
                }
            }
        }

        if (_ball == null && hadBallReference)
        {
            if (_hasSeenBall)
            {
                if (now - _ballLastActiveTime <= BallStateGraceSeconds)
                    return _lastMatch3DActive;
                return false;
            }
        }

        if (_hasSeenBall)
            return false;

        if (_matchBuilder == null)
        {
            var builderGo = GameObject.Find(MatchBuilderRoot);
            _matchBuilder = builderGo != null ? builderGo.transform : null;
        }

        if (_matchBuilder == null)
            return false;

        var builderObject = _matchBuilder.gameObject;
        if (builderObject == null)
        {
            _matchBuilder = null;
            return false;
        }

        return builderObject.activeInHierarchy;
    }

    private void RememberCurrentCustomState()
    {
        if (!_customCameraActive || _cameraMode == CameraMode.None)
        {
            _pendingRestoreState = default;
            return;
        }

        _pendingRestoreState = new CustomCameraState
        {
            HasState = true,
            Mode = _cameraMode,
            AutoFollowBallCarrier = _autoFollowBallCarrier,
            PlayerInstanceId = _currentPlayer?.InstanceId ?? -1,
            ManagerInstanceId = _currentManager?.InstanceId ?? -1
        };
    }

    private void RestorePendingCustomState()
    {
        var state = _pendingRestoreState;
        _pendingRestoreState = default;
        _restoreOnNext3D = false;

        if (!state.HasState)
            return;

        try
        {
            switch (state.Mode)
            {
                case CameraMode.PlayerPov:
                    if (!state.AutoFollowBallCarrier && state.PlayerInstanceId > 0)
                        TryRestorePlayerSelection(state.PlayerInstanceId, forceRefresh: true);

                    ActivatePlayerPov(state.AutoFollowBallCarrier);

                    if (!state.AutoFollowBallCarrier && state.PlayerInstanceId > 0)
                        TryRestorePlayerSelection(state.PlayerInstanceId, forceRefresh: false);
                    break;

                case CameraMode.ManagerPov:
                    if (state.ManagerInstanceId > 0)
                        TryRestoreManagerSelection(state.ManagerInstanceId, forceRefresh: true);

                    ActivateManagerPov();

                    if (state.ManagerInstanceId > 0)
                        TryRestoreManagerSelection(state.ManagerInstanceId, forceRefresh: false);
                    break;

                default:
                    break;
            }

            var restored = _customCameraActive && _cameraMode == state.Mode && state.Mode != CameraMode.None;
            if (restored)
                ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Restored {state.Mode} POV after 3D view returned.");
            else
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: 3D view returned; keeping default camera.");
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Failed to restore POV after 3D return: {ex}");
        }
    }

    private void TryRestorePlayerSelection(int instanceId, bool forceRefresh)
    {
        if (instanceId <= 0)
            return;

        EnsurePlayerList(forceRefresh);

        if (_players.Count == 0)
            return;

        var index = _players.FindIndex(p => p.InstanceId == instanceId);
        if (index < 0)
            return;

        _currentPlayerIndex = index;
        _currentPlayer = _players[index];
        _autoFollowBallCarrier = false;
    }

    private void TryRestoreManagerSelection(int instanceId, bool forceRefresh)
    {
        if (instanceId <= 0)
            return;

        EnsureManagerList(forceRefresh);

        if (_managers.Count == 0)
            return;

        var index = _managers.FindIndex(m => m.InstanceId == instanceId);
        if (index < 0)
            return;

        _currentManagerIndex = index;
        _currentManager = _managers[index];
        _loggedMissingManagerOnce = false;
    }

    private Vector3 GetStableBallGroundPosition(Vector3 fallback)
    {
        var groundY = _matchBuilder != null ? _matchBuilder.position.y : 0f;
        var fallbackGround = new Vector3(fallback.x, groundY, fallback.z);
        var deltaTime = Time.unscaledDeltaTime;
        var lerpFactor = deltaTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / ManagerBallFollowSmoothTime);

        if (_ball != null)
        {
            var target = _ball.position;
            target.y = groundY;

            if (!_hasSmoothedBall)
            {
                _smoothedBallPosition = target;
                _hasSmoothedBall = true;
                return _smoothedBallPosition;
            }

            _smoothedBallPosition = Vector3.Lerp(_smoothedBallPosition, target, lerpFactor);
            return _smoothedBallPosition;
        }

        if (!_hasSmoothedBall)
        {
            _smoothedBallPosition = fallbackGround;
            _hasSmoothedBall = true;
            return _smoothedBallPosition;
        }

        _smoothedBallPosition = Vector3.Lerp(_smoothedBallPosition, fallbackGround, lerpFactor);
        return _smoothedBallPosition;
    }

    private void ClearPendingRestore()
    {
        _restoreOnNext3D = false;
        _pendingRestoreState = default;
    }

    private void EnsureBallReference()
    {
        if (_ball != null)
        {
            var ballObject = _ball.gameObject;
            if (ballObject == null)
            {
                _ball = null;
            }
            else if (!_hasSeenBall)
            {
                _hasSeenBall = true;
                if (ballObject.activeSelf && ballObject.activeInHierarchy)
                    _ballLastActiveTime = Time.unscaledTime;
            }
        }

        if (_ball == null)
        {
            GameObject ballGo = null;
            foreach (var path in BallObjectPaths)
            {
                ballGo = GameObject.Find(path);
                if (ballGo != null)
                    break;
            }

            if (ballGo == null)
                ballGo = FindBallInMatchBuilder();

            _ball = ballGo != null ? ballGo.transform : null;
            if (_ball != null)
            {
                _hasSeenBall = true;
                _ballLastActiveTime = Time.unscaledTime;
            }
        }
    }

    private GameObject FindBallInMatchBuilder()
    {
        if (_matchBuilder == null)
        {
            var builderGo = GameObject.Find(MatchBuilderRoot);
            _matchBuilder = builderGo != null ? builderGo.transform : null;
        }

        if (_matchBuilder == null)
            return null;

        foreach (var transform in EnumerateHierarchy(_matchBuilder, includeInactive: true))
        {
            if (transform == null)
                continue;

            var name = transform.name;
            if (string.IsNullOrEmpty(name))
                continue;

            foreach (var candidate in BallObjectNames)
            {
                if (string.Equals(name, candidate, StringComparison.Ordinal))
                    return transform.gameObject;
            }

            if (name.StartsWith("BallPrefab", StringComparison.Ordinal))
                return transform.gameObject;
        }

        return null;
    }

    private void EnsurePlayerList(bool forceRefresh = false)
    {
        var now = Time.time;
        if (forceRefresh || _players.Count == 0 || now >= _nextPlayerScanTime)
        {
            _nextPlayerScanTime = now + 1.5f;
            RefreshPlayerList();
        }
        else
        {
            _players.RemoveAll(p => !p.IsValid);
        }
    }

    private void EnsureManagerList(bool forceRefresh = false)
    {
        var now = Time.time;
        if (forceRefresh || _managers.Count == 0 || now >= _nextManagerScanTime)
        {
            _nextManagerScanTime = now + 2f;
            RefreshManagerList();
        }
        else
        {
            _managers.RemoveAll(m => !m.IsValid);
        }
    }

    private void RefreshPlayerList()
    {
        try
        {
            if (_matchBuilder == null)
            {
                var builderGo = GameObject.Find(MatchBuilderRoot);
                _matchBuilder = builderGo != null ? builderGo.transform : null;
            }

            if (_matchBuilder == null)
                return;

            var candidates = new List<Avatar>();
            var seen = new HashSet<int>();

            foreach (var transform in EnumerateHierarchy(_matchBuilder, includeInactive: true))
            {
                if (transform == _matchBuilder)
                    continue;

                var animator = SafeGetComponent<Animator>(transform);
                if (animator == null)
                    continue;

                var root = animator.transform;
                if (!root.gameObject.activeInHierarchy)
                    continue;

                if (!IsLikelyPlayer(root))
                    continue;

                var instanceId = root.gameObject.GetInstanceID();
                if (!seen.Add(instanceId))
                    continue;
                var head = TryGetHead(animator);
                if (head == null)
                    head = FindHeadTransform(root);

                var displayName = CleanDisplayName(root.gameObject.name);
                candidates.Add(new Avatar
                {
                    Root = root,
                    Head = head,
                    DisplayName = displayName,
                    InstanceId = instanceId
                });
            }

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                _players.Clear();
                _players.AddRange(candidates);

                if (_currentPlayer != null)
                {
                    var index = _players.FindIndex(p => p.InstanceId == _currentPlayer.InstanceId);
                    if (index >= 0)
                    {
                        _currentPlayerIndex = index;
                        _currentPlayer = _players[index];
                    }
                    else if (_autoFollowBallCarrier)
                    {
                        _currentPlayer = null;
                        _currentPlayerIndex = -1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: RefreshPlayerList failed: {ex}");
        }
    }

    private void RefreshManagerList()
    {
        try
        {
            if (_matchBuilder == null)
            {
                var builderGo = GameObject.Find(MatchBuilderRoot);
                _matchBuilder = builderGo != null ? builderGo.transform : null;
            }

            if (_matchBuilder == null)
                return;

            var candidates = new List<Avatar>();
            var seen = new HashSet<int>();

            foreach (var transform in EnumerateHierarchy(_matchBuilder, includeInactive: true))
            {
                if (transform == _matchBuilder)
                    continue;

                var animator = SafeGetComponent<Animator>(transform);
                if (animator == null)
                    continue;

                var root = animator.transform;
                if (!root.gameObject.activeInHierarchy)
                    continue;

                if (!IsLikelyManager(root))
                    continue;

                var instanceId = root.gameObject.GetInstanceID();
                if (!seen.Add(instanceId))
                    continue;
                var head = TryGetHead(animator);
                if (head == null)
                    head = FindHeadTransform(root);

                var displayName = CleanDisplayName(root.gameObject.name);
                candidates.Add(new Avatar
                {
                    Root = root,
                    Head = head,
                    DisplayName = displayName,
                    InstanceId = instanceId
                });
            }

            _managers.Clear();

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                _managers.AddRange(candidates);

                if (_currentManager != null)
                {
                    var index = _managers.FindIndex(m => m.InstanceId == _currentManager.InstanceId);
                    if (index >= 0)
                    {
                        _currentManagerIndex = index;
                        _currentManager = _managers[index];
                    }
                    else
                    {
                        _currentManagerIndex = 0;
                        _currentManager = _managers[0];
                    }
                }
                else if (_managers.Count > 0)
                {
                    _currentManagerIndex = 0;
                    _currentManager = _managers[0];
                }
            }
            else
            {
                _currentManager = null;
                _currentManagerIndex = -1;
            }
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: RefreshManagerList failed: {ex}");
        }
    }

    private Avatar FindClosestPlayerToBall()
    {
        if (_ball == null || _players.Count == 0)
            return null;

        var ballPosition = _ball.position;
        Avatar closest = null;
        var bestDistance = float.MaxValue;

        foreach (var player in _players)
        {
            if (!player.IsValid)
                continue;

            var head = GetAvatarHeadPosition(player);
            var distance = (head - ballPosition).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                closest = player;
            }
        }

        return closest;
    }

    private void SetCurrentPlayer(Avatar candidate)
    {
        if (candidate == null || !candidate.IsValid)
            return;

        var index = _players.FindIndex(p => p.InstanceId == candidate.InstanceId);
        if (index >= 0)
        {
            _currentPlayerIndex = index;
            _currentPlayer = _players[index];
        }
        else
        {
            _currentPlayer = candidate;
            _currentPlayerIndex = -1;
        }
    }

    private Vector3 GetAvatarHeadPosition(Avatar player)
    {
        if (player == null)
            return Vector3.zero;

        if (player.Head != null)
            return player.Head.position;

        if (player.Root != null)
            return player.Root.position + Vector3.up * 1.7f;

        return Vector3.zero;
    }

    private Transform FindHeadTransform(Transform root)
    {
        if (root == null)
            return null;

        foreach (var child in EnumerateHierarchy(root, includeInactive: true))
        {
            if (child == root)
                continue;

            if (string.IsNullOrEmpty(child.name))
                continue;

            if (child.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0)
                return child;
        }

        return null;
    }

    private bool IsLikelyPlayer(Transform candidate)
    {
        if (candidate == null)
            return false;

        if (MatchesPlayerKeyword(candidate.name))
            return true;

        var parent = candidate.parent;
        var depth = 0;
        while (parent != null && depth < 3)
        {
            if (MatchesPlayerKeyword(parent.name))
                return true;
            parent = parent.parent;
            depth++;
        }

        return false;
    }

    private bool IsLikelyManager(Transform candidate)
    {
        if (candidate == null)
            return false;

        if (MatchesManagerKeyword(candidate.name))
            return true;

        var parent = candidate.parent;
        var depth = 0;
        while (parent != null && depth < 4)
        {
            if (MatchesManagerKeyword(parent.name))
                return true;
            parent = parent.parent;
            depth++;
        }

        return false;
    }

    private bool MatchesPlayerKeyword(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var lower = value.ToLowerInvariant();
        return lower.Contains("player") ||
               lower.Contains("goalkeeper") ||
               lower.Contains("goalie") ||
               lower.Contains("forward") ||
               lower.Contains("striker") ||
               lower.Contains("winger") ||
               lower.Contains("midfield") ||
               lower.Contains("midfielder") ||
               lower.Contains("defender") ||
               lower.Contains("attacker");
    }

    private bool MatchesManagerKeyword(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var lower = value.ToLowerInvariant();
        return lower.Contains("manager") ||
               lower.Contains("coach") ||
               lower.Contains("staff") ||
               lower.Contains("gaffer") ||
               lower.Contains("assistant") ||
               lower.Contains("technical") ||
               lower.Contains("touchline") ||
               lower.Contains("sideline");
    }

    private string CleanDisplayName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "Unknown Person";

        var cleaned = raw.Replace("(Clone)", string.Empty);
        cleaned = cleaned.Replace("_", " ");
        return cleaned.Trim();
    }

    private Transform TryGetHead(Animator animator)
    {
        if (animator == null)
            return null;

        try
        {
            if (animator.isHuman)
            {
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null)
                    return head;
            }
        }
        catch
        {
            // ignore IL2CPP issues
        }

        return null;
    }

    private IEnumerable<Transform> EnumerateHierarchy(Transform root, bool includeInactive)
    {
        if (root == null)
            yield break;

        var stack = new Stack<Transform>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current != root)
            {
                if (includeInactive || current.gameObject.activeInHierarchy)
                    yield return current;
            }

            for (int i = current.childCount - 1; i >= 0; i--)
            {
                var child = current.GetChild(i);
                if (child != null)
                    stack.Push(child);
            }
        }
    }

    private T SafeGetComponent<T>(Transform transform) where T : Component
    {
        if (transform == null)
            return null;

        try
        {
            return transform.GetComponent<T>();
        }
        catch
        {
            return null;
        }
    }

    private void OnGUI()
    {
        if (!_customCameraActive)
            return;

        string label = null;
        if (_cameraMode == CameraMode.PlayerPov)
        {
            if (_currentPlayer != null && !string.IsNullOrEmpty(_currentPlayer.DisplayName))
            {
                label = _currentPlayer.DisplayName;
            }
            else
            {
                EnsureBallReference();
                EnsurePlayerList();
                var carrier = FindClosestPlayerToBall();
                if (carrier != null && !string.IsNullOrEmpty(carrier.DisplayName))
                    label = carrier.DisplayName;
            }
        }
        else if (_cameraMode == CameraMode.ManagerPov)
        {
            EnsureBallReference();
            EnsurePlayerList();
            var focus = FindClosestPlayerToBall();
            if (focus != null && !string.IsNullOrEmpty(focus.DisplayName))
                label = focus.DisplayName;
        }

        if (!string.IsNullOrEmpty(label))
        {
            var canonical = label.Trim();
            var lower = canonical.ToLowerInvariant();
            var squashed = lower.Replace(" ", string.Empty);
            if (lower.Contains("player 3d") ||
                lower.Contains("player-3d") ||
                lower.Contains("player_3d") ||
                squashed.Contains("player3d"))
            {
                return;
            }
        }

        if (string.IsNullOrEmpty(label))
            return;

        if (_hudStyle == null)
        {
            _hudStyle = new GUIStyle();

            if (GUI.skin != null && GUI.skin.label != null)
            {
                _hudStyle.font = GUI.skin.label.font;
                _hudStyle.richText = GUI.skin.label.richText;
            }

            _hudStyle.fontSize = 24;
            _hudStyle.alignment = TextAnchor.UpperRight;
            _hudStyle.fontStyle = FontStyle.Bold;
            _hudStyle.normal.textColor = Color.white;
        }

        var width = 280f;
        var rectX = Mathf.Max(Screen.width * 0.75f - width, 0f);
        var rect = new Rect(rectX, 20f, width, 28f);
        GUI.Label(rect, label, _hudStyle);
    }
    
    private bool EnableCustomCamera()
    {
        string previousCustomTag = null;
        try
        {
            if (_customCamera != null)
            {
                previousCustomTag = _customCamera.tag;
                if (_customCamera.tag == "MainCamera")
                {
                    _customCamera.tag = "Untagged";
                }
            }

            // NOTE: IL2CPP runtime used by FM26 strips many reflection helpers
            // (FindObjectsOfType<T>, Camera.allCameras, Scene.GetRootGameObjects, etc.).
            // Always rely on the simple Camera.main/tag lookup to avoid MissingMethodException.
            var mainCameraCandidate = Camera.main;
            if (mainCameraCandidate == _customCamera)
            {
                mainCameraCandidate = null;
            }

            if (mainCameraCandidate == null)
            {
                var mainTagged = GameObject.FindWithTag("MainCamera");
                if (mainTagged != null)
                {
                    var cam = SafeGetComponent<Camera>(mainTagged.transform);
                    if (cam != null && cam != _customCamera)
                        mainCameraCandidate = cam;
                }
            }

            if (mainCameraCandidate == null && _lastMainCamera != null && _lastMainCamera != _customCamera)
            {
                mainCameraCandidate = _lastMainCamera;
            }

            _lastMainCamera = mainCameraCandidate;
            EnsureBallReference();

            if (_customCamera == null)
            {
                var go = new GameObject("ArthurRayCam_Custom");
                _customCamera = go.AddComponent<Camera>();
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.tag = "MainCamera";
            }
            else
            {
                _customCamera.tag = "MainCamera";
            }

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
                    _customCamera.depth = _lastMainCamera.depth + 1f;
                }
                catch
                {
                    /* ignore per-field copy issues on IL2CPP */
                }
            }

            _customCamera.transform.SetParent(null, worldPositionStays: true);

            if (_lastMainCamera != null)
            {
                _originalMainEnabled = _lastMainCamera.enabled;
                _originalMainTag = _lastMainCamera.tag;

                try
                {
                    _lastMainCamera.tag = "MainCamera";
                    _lastMainCamera.enabled = false;
                }
                catch
                {
                }
            }

            _customCamera.gameObject.SetActive(true);
            _customCamera.enabled = true;
            _customCameraActive = true;
            return true;
        }
        catch (Exception ex)
        {
            if (_customCamera != null && previousCustomTag != null)
            {
                _customCamera.tag = previousCustomTag;
            }
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: EnableCustomCamera failed: {ex}");
            _customCameraActive = false;
            return false;
        }
    }

    private void DisableCustomCamera(bool dueToSceneChange)
    {
        try
        {
            if (dueToSceneChange)
                ClearPendingRestore();

            if (_vrRig != null && _vrRigActive)
            {
                _vrRig.SetActive(false);
                _vrRigActive = false;
            }

            if (_customCamera != null)
            {
                _customCamera.enabled = false;
                _customCamera.gameObject.SetActive(false);
                _customCamera.transform.SetParent(null, worldPositionStays: true);
                _customCamera.tag = "Untagged";
            }

            var cameraToRestore = _lastMainCamera;
            var restoredEnabled = _originalMainEnabled;
            var restoredTag = string.IsNullOrEmpty(_originalMainTag) ? "MainCamera" : _originalMainTag;

            if (cameraToRestore == null)
            {
                cameraToRestore = Camera.main;
                if (cameraToRestore == _customCamera)
                    cameraToRestore = null;

                if (cameraToRestore == null)
                {
                    var mainTagged = GameObject.FindWithTag("MainCamera");
                    if (mainTagged != null)
                    {
                        var candidate = SafeGetComponent<Camera>(mainTagged.transform);
                        if (candidate != null && candidate != _customCamera)
                            cameraToRestore = candidate;
                    }
                }

                if (cameraToRestore != null)
                {
                    restoredEnabled = true;
                    restoredTag = "MainCamera";
                }
            }

            if (cameraToRestore != null)
            {
                try
                {
                    cameraToRestore.gameObject.SetActive(true);
                    cameraToRestore.tag = restoredTag;
                    cameraToRestore.enabled = true;
                }
                catch
                {
                }
            }
            else
            {
                ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: Unable to locate a main camera to restore after disabling custom camera.");
            }

            _customCameraActive = false;
            _cameraMode = CameraMode.None;
            _autoFollowBallCarrier = false;
            _currentPlayer = null;
            _currentPlayerIndex = -1;
            _currentManager = null;
            _currentManagerIndex = -1;
            _loggedMissingManagerOnce = false;

            if (dueToSceneChange)
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Scene change detected - custom camera deactivated.");
            else
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Custom camera deactivated; restored game camera.");
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: DisableCustomCamera failed: {ex}");
        }
        finally
        {
            _ball = null;
            _lastMainCamera = null;
            _matchBuilder = null;
            _originalMainTag = "MainCamera";
            _players.Clear();
            _managers.Clear();
            _hasSmoothedBall = false;
            _smoothedBallPosition = Vector3.zero;
        }
    }
}

public class VrRig
{
    private Camera _leftEyeCamera;
    private Camera _rightEyeCamera;
    private GameObject _rigRoot;
    public Transform _headTransform;
    private float _interpupillaryDistance = 0.064f;
    private float _fieldOfView = 96f;
    private bool _isActive;

    public void Initialize(Camera sourceCamera)
    {
        if (sourceCamera == null)
            return;

        _rigRoot = new GameObject("VRCameraRig");
        _headTransform = _rigRoot.transform;

        // Create left eye camera
        var leftEyeGo = new GameObject("LeftEye");
        leftEyeGo.transform.SetParent(_headTransform);
        _leftEyeCamera = leftEyeGo.AddComponent<Camera>();

        // Create right eye camera
        var rightEyeGo = new GameObject("RightEye");
        rightEyeGo.transform.SetParent(_headTransform);
        _rightEyeCamera = rightEyeGo.AddComponent<Camera>();

        // Copy settings from source camera
        CopyCameraSettings(sourceCamera, _leftEyeCamera);
        CopyCameraSettings(sourceCamera, _rightEyeCamera);

        // Set up stereo rendering
        _leftEyeCamera.stereoTargetEye = StereoTargetEyeMask.Left;
        _rightEyeCamera.stereoTargetEye = StereoTargetEyeMask.Right;
        _rightEyeCamera.enabled = false; // Only left eye renders main, right eye renders for stereo

        UpdateEyePositions();
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (_rigRoot != null)
            _rigRoot.SetActive(active);

        if (active && _leftEyeCamera != null)
        {
            _leftEyeCamera.enabled = true;
            _leftEyeCamera.tag = "MainCamera";
        }
        else if (_leftEyeCamera != null)
        {
            _leftEyeCamera.enabled = false;
            _leftEyeCamera.tag = "Untagged";
        }
    }

    public void SetInterpupillaryDistance(float ipd)
    {
        _interpupillaryDistance = Mathf.Max(0.01f, ipd);
        UpdateEyePositions();
    }

    public void SetFieldOfView(float fov)
    {
        _fieldOfView = Mathf.Clamp(fov, 30f, 150f);
        if (_leftEyeCamera != null)
            _leftEyeCamera.fieldOfView = _fieldOfView;
        if (_rightEyeCamera != null)
            _rightEyeCamera.fieldOfView = _fieldOfView;
    }

    public void UpdateRig(Vector3 position, Quaternion rotation)
    {
        if (!_isActive || _headTransform == null)
            return;

        _headTransform.position = position;
        _headTransform.rotation = rotation;
    }

    private void UpdateEyePositions()
    {
        if (_leftEyeCamera != null && _rightEyeCamera != null)
        {
            var halfIpd = _interpupillaryDistance * 0.5f;
            _leftEyeCamera.transform.localPosition = new Vector3(-halfIpd, 0f, 0f);
            _rightEyeCamera.transform.localPosition = new Vector3(halfIpd, 0f, 0f);
        }
    }

    private void CopyCameraSettings(Camera source, Camera target)
    {
        if (source == null || target == null)
            return;

        target.fieldOfView = source.fieldOfView;
        target.nearClipPlane = source.nearClipPlane;
        target.farClipPlane = source.farClipPlane;
        target.allowHDR = source.allowHDR;
        target.allowMSAA = source.allowMSAA;
        target.clearFlags = source.clearFlags;
        target.backgroundColor = source.backgroundColor;
        target.cullingMask = source.cullingMask;
        target.depth = source.depth;
    }

    public void Cleanup()
    {
        SetActive(false);
        if (_rigRoot != null)
            Object.DestroyImmediate(_rigRoot);
    }
}
