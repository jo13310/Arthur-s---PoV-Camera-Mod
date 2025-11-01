# Arthur's PoV Camera Mod for Football Manager 26

A comprehensive camera mod for Football Manager 26 that provides multiple POV (Point of View) camera modes including player perspective, manager perspective, and **VR support**.

## Features

### Camera Modes
- **Normal Camera (F1)**: Return to default game camera
- **Ball Carrier POV (F2)**: Automatically follow the player currently holding the ball
- **Previous Player (F3)**: Cycle to previous player in manual mode
- **Next Player (F4)**: Cycle to next player in manual mode  
- **Manager POV (F5)**: View from manager's perspective on the sidelines
- **VR POV (F6)**: **NEW** - Virtual Reality stereo camera mode

### VR Features
- **Stereo Rendering**: Left/Right eye cameras for true 3D depth perception
- **Configurable IPD**: Adjustable Interpupillary Distance (default: 64mm)
- **VR Field of View**: Wider FOV optimized for VR (default: 96°)
- **Auto-follow Ball Carrier**: VR mode automatically tracks the player with the ball
- **Player-based Positioning**: Camera positioned at realistic player head height
- **Smooth Tracking**: Camera smoothly follows player movement and ball action

### Configuration
The mod includes several configurable parameters:

```csharp
public KeyCode normalCameraKey = KeyCode.F1;
public KeyCode ballCarrierPovKey = KeyCode.F2;
public KeyCode previousPlayerKey = KeyCode.F3;
public KeyCode nextPlayerKey = KeyCode.F4;
public KeyCode managerPovKey = KeyCode.F5;
public KeyCode vrPovKey = KeyCode.F6;
public float vrInterPupillaryDistance = 0.064f;  // 64mm default IPD
public float vrFieldOfView = 96f;                // 96° default VR FOV
public bool vrAutoFollowBallCarrier = true;        // Auto-track ball carrier in VR
public bool copySettingsFromMain = true;           // Copy settings from main camera
```

## Installation

1. Install BepInEx for Football Manager 26
2. Copy the mod files to your BepInEx plugins folder
3. Launch Football Manager 26
4. The mod will automatically load and be available in matches

## Usage

### Basic Controls
- Press **F6** during a match to activate VR mode
- Press **F1** to return to normal camera
- Use **F3/F4** to cycle between players (when not in auto-follow mode)
- Press **F2** for ball carrier auto-follow mode
- Press **F5** for manager sideline view

### VR Mode Specifics
- VR mode activates stereo rendering with left/right eye cameras
- Camera positioning follows player head position and orientation
- Automatically tracks the player closest to the ball
- Uses realistic player height and field positioning
- Smooth camera transitions and ball tracking

## Technical Implementation

### VR Architecture
The VR implementation is based on proven VR camera patterns with these components:

#### VrRig Class
- **Stereo Camera Setup**: Creates left and right eye cameras
- **IPD Configuration**: Adjustable eye separation for realistic depth
- **Camera Settings Management**: Copies main camera properties to VR cameras
- **Position Tracking**: Updates rig position and rotation in real-time

#### Camera Integration
- **Seamless Mode Switching**: Transitions between normal and VR modes
- **State Preservation**: Remembers camera settings across mode changes
- **Scene Awareness**: Automatically handles scene transitions and 2D/3D switches
- **Performance Optimized**: Efficient player detection and camera updates

#### Player Detection
- **Smart Avatar System**: Identifies players and managers on the field
- **Ball Carrier Tracking**: Automatically finds and follows the player with the ball
- **Head Position Calculation**: Realistic camera positioning based on player anatomy
- **Smooth Transitions**: Lerp-based movement for comfortable VR experience

### Key Components

1. **VrRig**: Core VR camera system
   - Stereo camera management
   - IPD and FOV configuration
   - Position/rotation updates

2. **ArthurRayPovBootstrap**: Main controller
   - Input handling and mode switching
   - Player detection and tracking
   - Camera state management

3. **Avatar System**: Player/Manager representation
   - Transform tracking
   - Head position calculation
   - Display name management

## Compatibility

- **Football Manager 26**: Fully compatible with latest version
- **IL2CPP Support**: Optimized for IL2CPP runtime
- **BepInEx Framework**: Standard BepInEx plugin architecture
- **Performance**: Minimal impact on game performance

## Troubleshooting

### VR Mode Issues
- Ensure VR headset is properly connected
- Check that SteamVR or OpenVR is running
- Verify IPD settings are comfortable for your eyes
- Try adjusting VR FOV if experiencing discomfort

### General Issues
- Make sure BepInEx is properly installed
- Check for conflicting camera mods
- Verify you're in a match (not in menus)
- Review BepInEx logs for error messages

## Credits

- **Base Development**: GerKo & Brululul
- **VR Implementation**: Enhanced with SubmersedVR-inspired architecture
- **Testing & Feedback**: Community contributors

## License

This mod is provided as-is for educational and entertainment purposes. Please respect the original game's terms of service.

## Changelog

### v1.1.0 - VR Release
- ✅ Added complete VR camera system
- ✅ Stereo rendering with left/right eye cameras
- ✅ Configurable IPD and FOV settings
- ✅ Auto-follow ball carrier in VR mode
- ✅ Realistic player head positioning
- ✅ Seamless VR mode integration
- ✅ Performance optimizations for smooth VR experience

### v1.0.0 - Initial Release
- ✅ Player POV camera modes
- ✅ Manager POV camera
- ✅ Ball carrier auto-follow
- ✅ Player cycling system
- ✅ Basic configuration options

---

**Enjoy the immersive VR football experience! 🏈⚽️🥽**
