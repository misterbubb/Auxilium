using RootMotion.FinalIK;
using Steamworks;
using UnityEngine;
using Valve.VR;
using Vertigo2;
using Vertigo2.Player;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Represents a remote player in the game world.
    /// Uses the game's VRIK IK system to animate the player body from synced head/hand positions.
    /// Falls back to primitive shapes if the real model can't be loaded.
    /// </summary>
    public class RemotePlayer : MonoBehaviour
    {
        #region Constants

        // Layers
        private const int DEFAULT_VISIBLE_BODY_LAYER = 11;
        
        // Interpolation
        private const float DEFAULT_INTERPOLATION_SPEED = 15f;
        
        // Logging intervals
        private const int SET_TARGET_LOG_INTERVAL = 60;
        private const int GRIP_LOG_INTERVAL = 120;
        
        // Visual settings
        private const float NAME_TAG_HEIGHT_OFFSET = 0.3f;
        private const float MUZZLE_FLASH_OFFSET = 0.1f;
        private const float MUZZLE_FLASH_LIFETIME = 0.1f;
        private const float MUZZLE_FLASH_SIZE = 0.15f;
        private const float MUZZLE_FLASH_LIGHT_INTENSITY = 2f;
        private const float MUZZLE_FLASH_LIGHT_RANGE = 3f;
        private const float MUZZLE_FLASH_DURATION = 0.05f;
        
        // Body dimensions (for primitive visuals)
        private const float BODY_WIDTH = 0.3f;
        private const float BODY_HEIGHT = 0.5f;
        private const float HEAD_SCALE = 0.2f;
        private const float HEAD_HEIGHT_SCALE = 0.15f;
        private const float HAND_SCALE = 0.08f;
        
        // VRIK settings
        private const float VRIK_BODY_POS_STIFFNESS = 0.5f;
        private const float VRIK_BODY_ROT_STIFFNESS = 0.5f;
        private const float VRIK_NECK_STIFFNESS = 0.5f;
        private const float VRIK_MAINTAIN_PELVIS = 0.2f;
        private const float BODY_ROTATION_LERP_FACTOR = 0.3f;

        #endregion

        #region Properties

        public CSteamID SteamId { get; private set; }
        public string PlayerName { get; private set; }
        public float CurrentHealth { get; private set; }
        public float MaxHealth { get; private set; }
        public bool IsDead { get; private set; }
        public string CurrentWeapon { get; private set; }
        public string CurrentScene { get; private set; }
        public string CharacterId { get; private set; }
        public bool IsSceneReady { get; private set; }

        #endregion

        #region Player Model and IK

        private GameObject _playerModel;
        private VRIK _vrik;
        private bool _usingRealModel = false;
        
        // IK targets we control
        private Transform _headTarget;
        private Transform _leftHandTarget;
        private Transform _rightHandTarget;
        
        // Body root transform (the BodyAnimator transform)
        private Transform _bodyRoot;

        #endregion

        #region Hand Animation

        private Vertigo2.Interaction.HandAnimator _leftHandAnimator;
        private Vertigo2.Interaction.HandAnimator _rightHandAnimator;
        
        // Hand grip/finger curl values (0 = open, 1 = closed)
        private float _leftThumbCurl, _leftIndexCurl, _leftMiddleCurl, _leftRingCurl, _leftPinkyCurl;
        private float _rightThumbCurl, _rightIndexCurl, _rightMiddleCurl, _rightRingCurl, _rightPinkyCurl;
        
        // Hand bone transforms for direct animation
        // SteamVR bone order: wrist, thumb(4), index(5), middle(5), ring(5), pinky(5) = 25 bones
        private Transform[] _leftHandBones;
        private Transform[] _rightHandBones;
        
        // Open/closed hand pose data (cached from game's HandAnimator)
        private Vector3[] _leftOpenBonePositions;
        private Quaternion[] _leftOpenBoneRotations;
        private Vector3[] _leftClosedBonePositions;
        private Quaternion[] _leftClosedBoneRotations;
        private Vector3[] _rightOpenBonePositions;
        private Quaternion[] _rightOpenBoneRotations;
        private Vector3[] _rightClosedBonePositions;
        private Quaternion[] _rightClosedBoneRotations;
        
        // Whether hand animation is initialized
        private bool _handAnimationInitialized = false;

        #endregion

        #region Weapons

        private string _leftWeaponId = "none";
        private string _rightWeaponId = "none";
        private GameObject _leftWeaponVisual;
        private GameObject _rightWeaponVisual;

        #endregion

        #region Fallback Primitive Visuals

        private Transform _head;
        private Transform _leftHand;
        private Transform _rightHand;
        private Transform _body;
        private Renderer _headRenderer;
        private Renderer _leftHandRenderer;
        private Renderer _rightHandRenderer;
        private Renderer _bodyRenderer;

        #endregion

        #region Effects

        private ParticleSystem _muzzleFlash;

        #endregion

        #region Interpolation

        private Vector3 _targetRootPosition;
        private Vector3 _targetHeadPos;
        private Quaternion _targetHeadRot = Quaternion.identity;
        private Vector3 _targetLeftHandPos;
        private Quaternion _targetLeftHandRot = Quaternion.identity;
        private Vector3 _targetRightHandPos;
        private Quaternion _targetRightHandRot = Quaternion.identity;
        private float _interpolationSpeed = DEFAULT_INTERPOLATION_SPEED;

        #endregion

        #region UI

        private TextMesh _nameTag;

        #endregion

        #region State

        private float _lastUpdateTime;
        private int _visibleBodyLayer = DEFAULT_VISIBLE_BODY_LAYER;
        private Transform[] _headTransformsToFix;
        private SkinnedMeshRenderer[] _headRenderersToFix;
        private int _setTargetLogCounter = 0;
        private int _gripLogCounter = 0;

        #endregion

        public void Initialize(CSteamID steamId)
        {
            Plugin.Log.LogInfo($"[REMOTE INIT] ========== INITIALIZING REMOTE PLAYER ==========");
            Plugin.Log.LogInfo($"[REMOTE INIT] Steam ID: {steamId}");
            
            SteamId = steamId;
            PlayerName = SteamFriends.GetFriendPersonaName(steamId);
            CurrentHealth = 100f;
            MaxHealth = 100f;
            IsDead = false;
            CurrentScene = "";
            _lastUpdateTime = Time.time;
            
            Plugin.Log.LogInfo($"[REMOTE INIT] Player Name: {PlayerName}");
            Plugin.Log.LogInfo($"[REMOTE INIT] Transform position: {transform.position}");
            
            // Initialize targets to a reasonable default position (will be updated by network)
            _targetRootPosition = transform.position;
            _targetHeadPos = transform.position + Vector3.up * 1.6f;
            _targetHeadRot = Quaternion.identity;
            _targetLeftHandPos = transform.position + Vector3.up * 1.0f + Vector3.left * 0.3f;
            _targetLeftHandRot = Quaternion.identity;
            _targetRightHandPos = transform.position + Vector3.up * 1.0f + Vector3.right * 0.3f;
            _targetRightHandRot = Quaternion.identity;
            
            Plugin.Log.LogInfo($"[REMOTE INIT] Initial targets - Head: {_targetHeadPos}, LHand: {_targetLeftHandPos}, RHand: {_targetRightHandPos}");
            
            // Try to create real player model first, fall back to primitives
            Plugin.Log.LogInfo($"[REMOTE INIT] Attempting to create real player model...");
            if (!TryCreateRealPlayerModel())
            {
                Plugin.Log.LogInfo($"[REMOTE INIT] Real model failed, using primitive visuals for {PlayerName}");
                CreatePrimitiveVisuals();
            }
            else
            {
                Plugin.Log.LogInfo($"[REMOTE INIT] Successfully created real player model for {PlayerName}");
            }
            
            Plugin.Log.LogInfo($"[REMOTE INIT] Using real model: {_usingRealModel}");
            Plugin.Log.LogInfo($"[REMOTE INIT] ================================================");
        }

        private bool TryCreateRealPlayerModel()
        {
            try
            {
                Plugin.Log.LogInfo($"[MODEL] TryCreateRealPlayerModel starting...");
                Plugin.Log.LogInfo($"[MODEL] GameManager.instance: {GameManager.instance != null}");
                Plugin.Log.LogInfo($"[MODEL] GameManager.CurrentCharacter: {GameManager.CurrentCharacter != null}");
                
                if (GameManager.instance == null || GameManager.CurrentCharacter == null)
                {
                    Plugin.Log.LogWarning($"[MODEL] GameManager or CurrentCharacter is null");
                    return false;
                }
                
                Plugin.Log.LogInfo($"[MODEL] Current character: {GameManager.CurrentCharacter.id} ({GameManager.CurrentCharacter.name})");
                return TryCreatePlayerModel(GameManager.CurrentCharacter);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[MODEL] Failed to create real player model: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private bool TryCreatePlayerModel(PlayableCharacter character)
        {
            GameObject tempModel = null;
            bool success = false;
            
            try
            {
                Plugin.Log.LogInfo($"[MODEL] TryCreatePlayerModel for character: {character?.id ?? "NULL"}");
                
                if (character == null || character.prefab == null)
                {
                    Plugin.Log.LogWarning("[MODEL] Character or prefab is null");
                    return false;
                }

                Plugin.Log.LogInfo($"[MODEL] Character prefab: {character.prefab.name}");
                
                // The player prefab is HUGE - it contains cameras, UI, menus, etc.
                // We only need the body model which is under Playspace/[CharacterName]
                // Let's find and extract just that part
                
                // First, find the BodyAnimator in the prefab to locate the body
                var prefabBodyAnimator = character.prefab.GetComponentInChildren<BodyAnimator>(true);
                if (prefabBodyAnimator == null)
                {
                    Plugin.Log.LogWarning("[MODEL] No BodyAnimator found in character prefab");
                    return false;
                }
                
                // The body is the BodyAnimator's GameObject (e.g., "Sonja")
                var bodyPrefab = prefabBodyAnimator.gameObject;
                
                Plugin.Log.LogInfo($"[MODEL] Found body prefab: {bodyPrefab.name}");
                Plugin.Log.LogInfo($"[MODEL] Body prefab has VRIK: {bodyPrefab.GetComponent<VRIK>() != null}");
                
                // Instantiate ONLY the body part, not the whole player prefab
                // This avoids instantiating cameras, UI, menus, etc.
                tempModel = Object.Instantiate(bodyPrefab, transform);
                tempModel.name = $"RemotePlayerBody_{character.id}";
                tempModel.SetActive(false); // Keep inactive while we set up
                
                Plugin.Log.LogInfo($"[MODEL] Instantiated body model: {tempModel.name}");
                
                // Find VRIK in our instantiated body
                _vrik = tempModel.GetComponent<VRIK>();
                if (_vrik == null)
                {
                    Plugin.Log.LogWarning("[MODEL] No VRIK found on body - trying to add one");
                    // VRIK might not be on the body itself, try to set up without it
                    return false;
                }
                
                Plugin.Log.LogInfo($"[MODEL] Found VRIK component on body");
                
                _bodyRoot = tempModel.transform;
                
                // Clean up only problematic components - keep VRIK, Animator, renderers, transforms
                CleanupBodyComponents(tempModel);
                
                // Create IK targets BEFORE configuring VRIK
                CreateIKTargets();
                Plugin.Log.LogInfo($"[MODEL] Created IK targets - Head: {_headTarget != null}, LHand: {_leftHandTarget != null}, RHand: {_rightHandTarget != null}");
                
                // Configure VRIK to use our targets
                ConfigureVRIK();
                
                // Make sure all renderers are visible
                EnsureRenderersVisible();
                
                _usingRealModel = true;
                _playerModel = tempModel;
                tempModel = null; // Transfer ownership
                
                // NOW activate the model
                _playerModel.SetActive(true);
                Plugin.Log.LogInfo($"[MODEL] Activated player model");
                
                // Create name tag above head
                CreateNameTag(_headTarget);
                
                // Create muzzle flash on right hand
                CreateMuzzleFlash(_rightHandTarget);
                
                Plugin.Log.LogInfo($"[MODEL] Successfully created VRIK-based player model for {PlayerName}");
                
                // Log the full hierarchy for debugging hand sync
                LogFullHierarchy();
                
                success = true;
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[MODEL] Failed to create player model: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                // Clean up on failure
                if (!success && tempModel != null)
                {
                    Object.DestroyImmediate(tempModel);
                }
            }
        }
        
        private void CleanupBodyComponents(GameObject body)
        {
            // Only destroy components that will cause issues
            // IMPORTANT: Keep VRIK, Animator, all renderers, and transforms
            // The key is to NOT destroy anything that VRIK references
            var toDestroy = new System.Collections.Generic.List<Component>();
            
            int keptCount = 0;
            int destroyCount = 0;
            
            foreach (var comp in body.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                
                var type = comp.GetType();
                var typeName = type.Name;
                
                // ALWAYS keep these - they're essential for rendering and IK
                if (comp is Transform) { keptCount++; continue; }
                if (comp is VRIK) { keptCount++; continue; }
                if (comp is Animator) { keptCount++; continue; }
                if (comp is SkinnedMeshRenderer) { keptCount++; continue; }
                if (comp is MeshRenderer) { keptCount++; continue; }
                if (comp is MeshFilter) { keptCount++; continue; }
                
                // Keep TwistRelaxer - it's part of the IK system
                if (typeName == "TwistRelaxer") { keptCount++; continue; }
                
                // Destroy everything else - but be careful
                toDestroy.Add(comp);
                destroyCount++;
            }
            
            // Disable first to prevent errors during destruction
            foreach (var comp in toDestroy)
            {
                if (comp is Behaviour b) 
                {
                    try { b.enabled = false; } catch { }
                }
            }
            
            // Now destroy
            foreach (var comp in toDestroy)
            {
                if (comp != null)
                {
                    try
                    {
                        Object.DestroyImmediate(comp);
                    }
                    catch
                    {
                        // Some components can't be destroyed due to dependencies, that's ok
                    }
                }
            }
            
            Plugin.Log.LogInfo($"Body cleanup: kept {keptCount} components, destroyed {destroyCount} components");
        }
        
        private void LogHierarchy(Transform t, int depth)
        {
            string indent = new string(' ', depth * 2);
            
            // Get all components on this GameObject
            var components = t.GetComponents<Component>();
            var componentNames = new System.Collections.Generic.List<string>();
            foreach (var c in components)
            {
                if (c != null)
                    componentNames.Add(c.GetType().Name);
            }
            
            string compStr = componentNames.Count > 0 ? $" [{string.Join(", ", componentNames)}]" : "";
            Plugin.Log.LogInfo($"{indent}{t.name}{compStr}");
            
            // Recurse to children
            foreach (Transform child in t)
            {
                LogHierarchy(child, depth + 1);
            }
        }
        
        /// <summary>
        /// Log the full hierarchy of the player model for debugging hand/body sync
        /// This helps identify bone names and structure for proper animation
        /// </summary>
        private void LogFullHierarchy()
        {
            if (_playerModel == null) return;
            
            Plugin.Log.LogInfo("=== REMOTE PLAYER MODEL HIERARCHY ===");
            Plugin.Log.LogInfo($"Player: {PlayerName}, Character: {CharacterId}");
            Plugin.Log.LogInfo("");
            
            // Log the full hierarchy
            LogHierarchy(_playerModel.transform, 0);
            
            Plugin.Log.LogInfo("");
            Plugin.Log.LogInfo("=== VRIK CONFIGURATION ===");
            
            if (_vrik != null)
            {
                var solver = _vrik.solver;
                Plugin.Log.LogInfo($"VRIK Initiated: {solver.initiated}");
                
                // Spine references
                Plugin.Log.LogInfo("--- Spine ---");
                if (solver.spine.headTarget != null)
                    Plugin.Log.LogInfo($"  Head Target: {solver.spine.headTarget.name}");
                if (solver.spine.pelvisTarget != null)
                    Plugin.Log.LogInfo($"  Pelvis Target: {solver.spine.pelvisTarget.name}");
                
                // Left arm references
                Plugin.Log.LogInfo("--- Left Arm ---");
                if (solver.leftArm.target != null)
                    Plugin.Log.LogInfo($"  Target: {solver.leftArm.target.name}");
                Plugin.Log.LogInfo($"  Position Weight: {solver.leftArm.positionWeight}");
                Plugin.Log.LogInfo($"  Rotation Weight: {solver.leftArm.rotationWeight}");
                
                // Right arm references
                Plugin.Log.LogInfo("--- Right Arm ---");
                if (solver.rightArm.target != null)
                    Plugin.Log.LogInfo($"  Target: {solver.rightArm.target.name}");
                Plugin.Log.LogInfo($"  Position Weight: {solver.rightArm.positionWeight}");
                Plugin.Log.LogInfo($"  Rotation Weight: {solver.rightArm.rotationWeight}");
                
                // Leg references
                Plugin.Log.LogInfo("--- Legs ---");
                Plugin.Log.LogInfo($"  Left Leg Position Weight: {solver.leftLeg.positionWeight}");
                Plugin.Log.LogInfo($"  Right Leg Position Weight: {solver.rightLeg.positionWeight}");
            }
            
            Plugin.Log.LogInfo("");
            Plugin.Log.LogInfo("=== SKINNED MESH RENDERERS ===");
            
            foreach (var smr in _playerModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Plugin.Log.LogInfo($"  {smr.gameObject.name}: layer={smr.gameObject.layer}, enabled={smr.enabled}, bones={smr.bones?.Length ?? 0}");
                
                // Log bone names for hand meshes
                string nameLower = smr.gameObject.name.ToLower();
                if (nameLower.Contains("hand") || nameLower.Contains("arm"))
                {
                    Plugin.Log.LogInfo($"    Bones for {smr.gameObject.name}:");
                    if (smr.bones != null)
                    {
                        foreach (var bone in smr.bones)
                        {
                            if (bone != null)
                                Plugin.Log.LogInfo($"      - {bone.name}");
                        }
                    }
                }
            }
            
            Plugin.Log.LogInfo("");
            Plugin.Log.LogInfo("=== HAND-RELATED TRANSFORMS ===");
            
            // Find all transforms that might be hand-related
            foreach (var t in _playerModel.GetComponentsInChildren<Transform>(true))
            {
                string nameLower = t.name.ToLower();
                if (nameLower.Contains("hand") || nameLower.Contains("wrist") || 
                    nameLower.Contains("finger") || nameLower.Contains("thumb") ||
                    nameLower.Contains("index") || nameLower.Contains("middle") ||
                    nameLower.Contains("ring") || nameLower.Contains("pinky") ||
                    nameLower.Contains("arm") || nameLower.Contains("elbow") ||
                    nameLower.Contains("shoulder"))
                {
                    string path = GetTransformPath(t);
                    Plugin.Log.LogInfo($"  {path}");
                }
            }
            
            Plugin.Log.LogInfo("=== END HIERARCHY ===");
            
            // Initialize hand animation after logging hierarchy
            InitializeHandAnimation();
        }
        
        /// <summary>
        /// Initialize hand bone animation by finding bones and caching pose data
        /// </summary>
        private void InitializeHandAnimation()
        {
            if (_handAnimationInitialized) return;
            
            Plugin.Log.LogInfo($"[HAND ANIM] ========== INITIALIZING HAND ANIMATION ==========");
            Plugin.Log.LogInfo($"[HAND ANIM] Player: {PlayerName}");
            Plugin.Log.LogInfo($"[HAND ANIM] Player model: {_playerModel?.name ?? "NULL"}");
            
            try
            {
                // Try to find HandAnimator components in the model
                var handAnimators = _playerModel.GetComponentsInChildren<Vertigo2.Interaction.HandAnimator>(true);
                Plugin.Log.LogInfo($"[HAND ANIM] Found {handAnimators.Length} HandAnimator components in model");
                
                foreach (var ha in handAnimators)
                {
                    Plugin.Log.LogInfo($"[HAND ANIM] HandAnimator: {ha.gameObject.name}, inputSource={ha.inputSource}");
                    if (ha.inputSource == Valve.VR.SteamVR_Input_Sources.LeftHand)
                        _leftHandAnimator = ha;
                    else if (ha.inputSource == Valve.VR.SteamVR_Input_Sources.RightHand)
                        _rightHandAnimator = ha;
                }
                
                Plugin.Log.LogInfo($"[HAND ANIM] Left HandAnimator: {_leftHandAnimator != null}");
                Plugin.Log.LogInfo($"[HAND ANIM] Right HandAnimator: {_rightHandAnimator != null}");
                
                // If we have HandAnimators, use their bone arrays
                if (_leftHandAnimator != null && _leftHandAnimator.bones != null)
                {
                    _leftHandBones = _leftHandAnimator.bones;
                    Plugin.Log.LogInfo($"[HAND ANIM] Left hand has {_leftHandBones.Length} bones from HandAnimator");
                    for (int i = 0; i < Mathf.Min(5, _leftHandBones.Length); i++)
                    {
                        Plugin.Log.LogInfo($"[HAND ANIM]   Bone {i}: {_leftHandBones[i]?.name ?? "NULL"}");
                    }
                }
                
                if (_rightHandAnimator != null && _rightHandAnimator.bones != null)
                {
                    _rightHandBones = _rightHandAnimator.bones;
                    Plugin.Log.LogInfo($"[HAND ANIM] Right hand has {_rightHandBones.Length} bones from HandAnimator");
                    for (int i = 0; i < Mathf.Min(5, _rightHandBones.Length); i++)
                    {
                        Plugin.Log.LogInfo($"[HAND ANIM]   Bone {i}: {_rightHandBones[i]?.name ?? "NULL"}");
                    }
                }
                
                // If no HandAnimators, try to find bones manually
                if (_leftHandBones == null || _leftHandBones.Length == 0)
                {
                    Plugin.Log.LogInfo($"[HAND ANIM] No left hand bones from HandAnimator, searching manually...");
                    _leftHandBones = FindHandBones("left");
                }
                if (_rightHandBones == null || _rightHandBones.Length == 0)
                {
                    Plugin.Log.LogInfo($"[HAND ANIM] No right hand bones from HandAnimator, searching manually...");
                    _rightHandBones = FindHandBones("right");
                }
                
                // Cache open/closed pose data from the local player's HandAnimator
                Plugin.Log.LogInfo($"[HAND ANIM] Caching hand pose data from local player...");
                CacheHandPoseData();
                
                int leftBoneCount = _leftHandBones != null ? CountNonNull(_leftHandBones) : 0;
                int rightBoneCount = _rightHandBones != null ? CountNonNull(_rightHandBones) : 0;
                bool hasLeftPoses = _leftOpenBonePositions != null && _leftClosedBonePositions != null;
                bool hasRightPoses = _rightOpenBonePositions != null && _rightClosedBonePositions != null;
                
                Plugin.Log.LogInfo($"[HAND ANIM] Left hand: {leftBoneCount} bones, poses cached: {hasLeftPoses}");
                Plugin.Log.LogInfo($"[HAND ANIM] Right hand: {rightBoneCount} bones, poses cached: {hasRightPoses}");
                
                _handAnimationInitialized = (leftBoneCount > 0 || rightBoneCount > 0);
                
                Plugin.Log.LogInfo($"[HAND ANIM] Initialization complete: {_handAnimationInitialized}");
                Plugin.Log.LogInfo($"[HAND ANIM] ================================================");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[HAND ANIM] Failed to initialize: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Count non-null elements in an array
        /// </summary>
        private int CountNonNull(Transform[] arr)
        {
            if (arr == null) return 0;
            int count = 0;
            foreach (var t in arr)
                if (t != null) count++;
            return count;
        }
        
        /// <summary>
        /// Find hand bones by searching for common bone names
        /// </summary>
        private Transform[] FindHandBones(string handSide)
        {
            // Sonja model uses naming: hand_L, thumb01_L, index00_L, etc.
            // We need to find bones in SteamVR order (25 bones):
            // 0: Wrist (hand_L/hand_R)
            // 1-4: Thumb (thumb01, thumb02, thumb03, end)
            // 5-9: Index (index00, index01, index02, index03, end)
            // 10-14: Middle (middle00, middle01, middle02, middle03, end)
            // 15-19: Ring (ring00, ring01, ring02, ring03, end)
            // 20-24: Pinky (pinky00, pinky01, pinky02, pinky03, end)
            
            string suffix = handSide.ToLower() == "left" ? "_L" : "_R";
            var bones = new Transform[25];
            
            // First, find the armature root - bones should be under Sonja_Armature, not BammModels
            Transform armatureRoot = null;
            foreach (var t in _playerModel.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.Contains("Armature") || t.name == "root")
                {
                    armatureRoot = t;
                    Plugin.Log.LogInfo($"[HAND ANIM] Found armature root: {t.name}");
                    break;
                }
            }
            
            if (armatureRoot == null)
            {
                Plugin.Log.LogWarning($"[HAND ANIM] No armature root found for {handSide} hand");
                return null;
            }
            
            // Find wrist (hand_L or hand_R) - must be under the armature, not BammModels
            string wristName = $"hand{suffix}";
            foreach (var t in armatureRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == wristName)
                {
                    bones[0] = t;
                    Plugin.Log.LogInfo($"[HAND ANIM] Found wrist: {t.name} at path {GetTransformPath(t)}");
                    break;
                }
            }
            
            if (bones[0] == null)
            {
                Plugin.Log.LogWarning($"[HAND ANIM] Could not find wrist bone {wristName} under armature");
                return null;
            }
            
            // Find finger bones starting from the wrist
            Transform wrist = bones[0];
            
            // Find thumb bones (indices 1-4)
            bones[1] = FindBoneUnderParent(wrist, $"thumb01{suffix}");
            bones[2] = FindBoneUnderParent(wrist, $"thumb02{suffix}");
            bones[3] = FindBoneUnderParent(wrist, $"thumb03{suffix}");
            bones[4] = FindChildNamed(bones[3], "end"); // thumb tip
            
            // Find index bones (indices 5-9)
            bones[5] = FindBoneUnderParent(wrist, $"index00{suffix}");
            bones[6] = FindBoneUnderParent(wrist, $"index01{suffix}");
            bones[7] = FindBoneUnderParent(wrist, $"index02{suffix}");
            bones[8] = FindBoneUnderParent(wrist, $"index03{suffix}");
            bones[9] = FindChildNamed(bones[8], "end"); // index tip
            
            // Find middle bones (indices 10-14)
            bones[10] = FindBoneUnderParent(wrist, $"middle00{suffix}");
            bones[11] = FindBoneUnderParent(wrist, $"middle01{suffix}");
            bones[12] = FindBoneUnderParent(wrist, $"middle02{suffix}");
            bones[13] = FindBoneUnderParent(wrist, $"middle03{suffix}");
            bones[14] = FindChildNamed(bones[13], "end"); // middle tip
            
            // Find ring bones (indices 15-19)
            bones[15] = FindBoneUnderParent(wrist, $"ring00{suffix}");
            bones[16] = FindBoneUnderParent(wrist, $"ring01{suffix}");
            bones[17] = FindBoneUnderParent(wrist, $"ring02{suffix}");
            bones[18] = FindBoneUnderParent(wrist, $"ring03{suffix}");
            bones[19] = FindChildNamed(bones[18], "end"); // ring tip
            
            // Find pinky bones (indices 20-24)
            bones[20] = FindBoneUnderParent(wrist, $"pinky00{suffix}");
            bones[21] = FindBoneUnderParent(wrist, $"pinky01{suffix}");
            bones[22] = FindBoneUnderParent(wrist, $"pinky02{suffix}");
            bones[23] = FindBoneUnderParent(wrist, $"pinky03{suffix}");
            bones[24] = FindChildNamed(bones[23], "end"); // pinky tip
            
            // Count found bones
            int foundCount = 0;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null) foundCount++;
            }
            
            Plugin.Log.LogInfo($"[HAND ANIM] Found {foundCount}/25 bones for {handSide} hand");
            
            // Log which bones were found/missing
            if (foundCount > 0 && foundCount < 25)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null)
                        Plugin.Log.LogWarning($"[HAND ANIM] Missing bone index {i} for {handSide} hand");
                }
            }
            
            return foundCount > 0 ? bones : null;
        }
        
        /// <summary>
        /// Find a bone by name under a parent transform (recursive)
        /// </summary>
        private Transform FindBoneUnderParent(Transform parent, string boneName)
        {
            if (parent == null) return null;
            
            foreach (var t in parent.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName)
                    return t;
            }
            return null;
        }
        
        /// <summary>
        /// Find a child transform by name (direct children only)
        /// </summary>
        private Transform FindChildNamed(Transform parent, string childName)
        {
            if (parent == null) return null;
            foreach (Transform child in parent)
            {
                if (child.name.ToLower() == childName.ToLower())
                    return child;
            }
            return null;
        }
        
        /// <summary>
        /// Cache hand pose data from the local player's HandAnimator
        /// </summary>
        private void CacheHandPoseData()
        {
            Plugin.Log.LogInfo($"[HAND ANIM] CacheHandPoseData starting...");
            
            // Try to get pose data from the local player's BodyAnimator
            if (VertigoPlayer.instance == null)
            {
                Plugin.Log.LogWarning($"[HAND ANIM] VertigoPlayer.instance is null, cannot cache poses");
                return;
            }
            
            var bodyAnim = VertigoPlayer.instance.GetComponentInChildren<BodyAnimator>(true);
            if (bodyAnim == null)
            {
                Plugin.Log.LogWarning($"[HAND ANIM] No BodyAnimator found on local player");
                return;
            }
            
            Plugin.Log.LogInfo($"[HAND ANIM] Found BodyAnimator: {bodyAnim.name}");
            Plugin.Log.LogInfo($"[HAND ANIM] handLeft: {bodyAnim.handLeft != null}, handRight: {bodyAnim.handRight != null}");
            
            // Get left hand poses
            if (bodyAnim.handLeft?.animator != null)
            {
                var leftAnim = bodyAnim.handLeft.animator;
                Plugin.Log.LogInfo($"[HAND ANIM] Left hand animator: {leftAnim.name}");
                CachePoseFromAnimator(leftAnim, true);
            }
            else
            {
                Plugin.Log.LogWarning($"[HAND ANIM] No left hand animator found");
            }
            
            // Get right hand poses
            if (bodyAnim.handRight?.animator != null)
            {
                var rightAnim = bodyAnim.handRight.animator;
                Plugin.Log.LogInfo($"[HAND ANIM] Right hand animator: {rightAnim.name}");
                CachePoseFromAnimator(rightAnim, false);
            }
            else
            {
                Plugin.Log.LogWarning($"[HAND ANIM] No right hand animator found");
            }
        }
        
        /// <summary>
        /// Cache open/closed pose data from a HandAnimator
        /// </summary>
        private void CachePoseFromAnimator(Vertigo2.Interaction.HandAnimator animator, bool isLeft)
        {
            try
            {
                // Get open hand pose (relaxed)
                if (animator.pose_openHand_relaxed != null)
                {
                    var openPose = isLeft ? animator.pose_openHand_relaxed.leftHand : animator.pose_openHand_relaxed.rightHand;
                    if (openPose != null && openPose.bonePositions != null)
                    {
                        if (isLeft)
                        {
                            _leftOpenBonePositions = (Vector3[])openPose.bonePositions.Clone();
                            _leftOpenBoneRotations = (Quaternion[])openPose.boneRotations.Clone();
                        }
                        else
                        {
                            _rightOpenBonePositions = (Vector3[])openPose.bonePositions.Clone();
                            _rightOpenBoneRotations = (Quaternion[])openPose.boneRotations.Clone();
                        }
                        Plugin.Log.LogInfo($"[HAND ANIM] Cached open pose for {(isLeft ? "left" : "right")} hand ({openPose.bonePositions.Length} bones)");
                    }
                }
                
                // Get closed hand pose (relaxed fist)
                if (animator.pose_closedHand_relaxed != null)
                {
                    var closedPose = isLeft ? animator.pose_closedHand_relaxed.leftHand : animator.pose_closedHand_relaxed.rightHand;
                    if (closedPose != null && closedPose.bonePositions != null)
                    {
                        if (isLeft)
                        {
                            _leftClosedBonePositions = (Vector3[])closedPose.bonePositions.Clone();
                            _leftClosedBoneRotations = (Quaternion[])closedPose.boneRotations.Clone();
                        }
                        else
                        {
                            _rightClosedBonePositions = (Vector3[])closedPose.bonePositions.Clone();
                            _rightClosedBoneRotations = (Quaternion[])closedPose.boneRotations.Clone();
                        }
                        Plugin.Log.LogInfo($"[HAND ANIM] Cached closed pose for {(isLeft ? "left" : "right")} hand ({closedPose.bonePositions.Length} bones)");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[HAND ANIM] Failed to cache pose from animator: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the full path of a transform from the model root
        /// </summary>
        private string GetTransformPath(Transform t)
        {
            if (t == null) return "null";
            
            var path = t.name;
            var parent = t.parent;
            while (parent != null && parent != _playerModel?.transform)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
        
        private void EnsureRenderersVisible()
        {
            if (_playerModel == null) return;
            
            // First, find what layer the Torso uses - that's the visible body layer
            int visibleBodyLayer = -1;
            foreach (var smr in _playerModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string nameLower = smr.gameObject.name.ToLower();
                if (nameLower.Contains("torso") || nameLower.Contains("body"))
                {
                    visibleBodyLayer = smr.gameObject.layer;
                    Plugin.Log.LogInfo($"[HEAD FIX] Found visible body layer from {smr.gameObject.name}: layer {visibleBodyLayer}");
                    break;
                }
            }
            
            // If we didn't find torso, use default layer (common body layer in Vertigo 2)
            if (visibleBodyLayer == -1)
            {
                visibleBodyLayer = DEFAULT_VISIBLE_BODY_LAYER;
                Plugin.Log.LogInfo($"[HEAD FIX] No torso found, using layer {DEFAULT_VISIBLE_BODY_LAYER} (common body layer)");
            }
            
            // Store the visible body layer for LateUpdate
            _visibleBodyLayer = visibleBodyLayer;
            
            // Enable all renderers and set head/eyes to same layer as torso
            foreach (var renderer in _playerModel.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = true;
                
                string nameLower = renderer.gameObject.name.ToLower();
                
                // Set head-related objects (head, eyes, helmet, visor) to the same layer as the torso
                if (nameLower.Contains("head") || nameLower.Contains("face") || nameLower.Contains("hair") ||
                    nameLower.Contains("eye") || nameLower.Contains("helmet") || nameLower.Contains("visor"))
                {
                    if (renderer.gameObject.layer != visibleBodyLayer)
                    {
                        Plugin.Log.LogInfo($"[HEAD FIX] Changing layer of {renderer.gameObject.name} from {renderer.gameObject.layer} to {visibleBodyLayer}");
                        renderer.gameObject.layer = visibleBodyLayer;
                    }
                }
                
                // Reset any scale that might have been set to zero (HideLegs does this)
                if (renderer.transform.localScale == Vector3.zero)
                {
                    Plugin.Log.LogInfo($"[HEAD FIX] Resetting scale of {renderer.gameObject.name} from zero to one");
                    renderer.transform.localScale = Vector3.one;
                }
            }
            
            // Find and disable HideLegs component if present
            foreach (var hideLegs in _playerModel.GetComponentsInChildren<HideLegs>(true))
            {
                Plugin.Log.LogInfo($"[HEAD FIX] Disabling HideLegs on {hideLegs.gameObject.name}");
                hideLegs.hidden = false;
                hideLegs.enabled = false;
            }
            
            // Make sure all SkinnedMeshRenderers are visible
            foreach (var smr in _playerModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.enabled = true;
                smr.updateWhenOffscreen = true;
                
                // Log head-related meshes for debugging
                string nameLower = smr.gameObject.name.ToLower();
                if (nameLower.Contains("head") || nameLower.Contains("face") || nameLower.Contains("hair") || nameLower.Contains("eye"))
                {
                    Plugin.Log.LogInfo($"[HEAD FIX] Head mesh: {smr.gameObject.name}, layer={smr.gameObject.layer}, enabled={smr.enabled}");
                }
            }
            
            // Also fix head transforms
            foreach (var t in _playerModel.GetComponentsInChildren<Transform>(true))
            {
                string nameLower = t.gameObject.name.ToLower();
                if (nameLower.Contains("head") || nameLower == "head" || nameLower.Contains("eye") || 
                    nameLower.Contains("helmet") || nameLower.Contains("visor"))
                {
                    // Set to visible body layer
                    if (t.gameObject.layer != visibleBodyLayer)
                    {
                        t.gameObject.layer = visibleBodyLayer;
                    }
                    
                    // Ensure scale is not zero
                    if (t.localScale == Vector3.zero)
                    {
                        Plugin.Log.LogInfo($"[HEAD FIX] Resetting scale of transform {t.gameObject.name} from zero to one");
                        t.localScale = Vector3.one;
                    }
                    
                    // Make sure the GameObject is active
                    if (!t.gameObject.activeSelf)
                    {
                        Plugin.Log.LogInfo($"[HEAD FIX] Activating {t.gameObject.name}");
                        t.gameObject.SetActive(true);
                    }
                }
            }
        }

        private void CreateIKTargets()
        {
            // Create a container for IK targets
            var targetsContainer = new GameObject("IKTargets");
            targetsContainer.transform.SetParent(transform);
            targetsContainer.transform.localPosition = Vector3.zero;
            targetsContainer.transform.localRotation = Quaternion.identity;
            
            // Head target
            var headObj = new GameObject("HeadTarget");
            headObj.transform.SetParent(targetsContainer.transform);
            _headTarget = headObj.transform;
            
            // Left hand target
            var leftHandObj = new GameObject("LeftHandTarget");
            leftHandObj.transform.SetParent(targetsContainer.transform);
            _leftHandTarget = leftHandObj.transform;
            
            // Right hand target
            var rightHandObj = new GameObject("RightHandTarget");
            rightHandObj.transform.SetParent(targetsContainer.transform);
            _rightHandTarget = rightHandObj.transform;
        }

        private void ConfigureVRIK()
        {
            Plugin.Log.LogInfo($"[VRIK] ========== CONFIGURING VRIK ==========");
            Plugin.Log.LogInfo($"[VRIK] VRIK component: {_vrik != null}");
            Plugin.Log.LogInfo($"[VRIK] Head target: {_headTarget != null}");
            Plugin.Log.LogInfo($"[VRIK] Left hand target: {_leftHandTarget != null}");
            Plugin.Log.LogInfo($"[VRIK] Right hand target: {_rightHandTarget != null}");
            
            // Set up the VRIK solver targets
            _vrik.solver.spine.headTarget = _headTarget;
            _vrik.solver.leftArm.target = _leftHandTarget;
            _vrik.solver.rightArm.target = _rightHandTarget;
            
            // Configure solver settings for smooth remote player animation
            _vrik.solver.spine.positionWeight = 1f;
            _vrik.solver.spine.rotationWeight = 1f;
            
            // Arm settings - full position/rotation tracking
            _vrik.solver.leftArm.positionWeight = 1f;
            _vrik.solver.leftArm.rotationWeight = 1f;
            _vrik.solver.leftArm.shoulderRotationWeight = 1f;
            _vrik.solver.leftArm.shoulderTwistWeight = 1f;
            
            _vrik.solver.rightArm.positionWeight = 1f;
            _vrik.solver.rightArm.rotationWeight = 1f;
            _vrik.solver.rightArm.shoulderRotationWeight = 1f;
            _vrik.solver.rightArm.shoulderTwistWeight = 1f;
            
            // Spine settings for better posture
            _vrik.solver.spine.pelvisPositionWeight = 0f; // Don't track pelvis directly
            _vrik.solver.spine.pelvisRotationWeight = 0f;
            _vrik.solver.spine.chestGoalWeight = 0f;
            _vrik.solver.spine.minHeadHeight = 0f; // Allow any head height
            _vrik.solver.spine.bodyPosStiffness = VRIK_BODY_POS_STIFFNESS;
            _vrik.solver.spine.bodyRotStiffness = VRIK_BODY_ROT_STIFFNESS;
            _vrik.solver.spine.neckStiffness = VRIK_NECK_STIFFNESS;
            _vrik.solver.spine.maintainPelvisPosition = VRIK_MAINTAIN_PELVIS;
            
            // Disable locomotion (we control position directly)
            _vrik.solver.locomotion.weight = 0f;
            
            // Leg settings - plant feet on ground
            _vrik.solver.leftLeg.positionWeight = 0f;
            _vrik.solver.leftLeg.rotationWeight = 0f;
            _vrik.solver.rightLeg.positionWeight = 0f;
            _vrik.solver.rightLeg.rotationWeight = 0f;
            
            // IMPORTANT: Enable VRIK so it can initialize and run
            _vrik.enabled = true;
            
            Plugin.Log.LogInfo($"[VRIK] VRIK enabled: {_vrik.enabled}");
            Plugin.Log.LogInfo($"[VRIK] Solver initiated before init: {_vrik.solver.initiated}");
            
            // Force initialization if not already done
            if (!_vrik.solver.initiated)
            {
                try
                {
                    _vrik.solver.Initiate(_vrik.transform);
                    Plugin.Log.LogInfo($"[VRIK] Manually initiated solver");
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[VRIK] Initialization warning: {ex.Message}");
                }
            }
            
            Plugin.Log.LogInfo($"[VRIK] Solver initiated after init: {_vrik.solver.initiated}");
            Plugin.Log.LogInfo($"[VRIK] Configured for {PlayerName}");
            Plugin.Log.LogInfo($"[VRIK] ========================================");
        }

        private void CreatePrimitiveVisuals()
        {
            Color playerColor = GetColorFromSteamId(SteamId);
            
            // Create body (cylinder for torso)
            var bodyObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bodyObj.name = "Body";
            bodyObj.transform.SetParent(transform);
            bodyObj.transform.localScale = new Vector3(BODY_WIDTH, BODY_HEIGHT, BODY_WIDTH);
            bodyObj.transform.localPosition = new Vector3(0, BODY_HEIGHT, 0);
            _body = bodyObj.transform;
            _bodyRenderer = bodyObj.GetComponent<Renderer>();
            DestroyCollider(bodyObj);

            // Create head (capsule)
            var headObj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            headObj.name = "Head";
            headObj.transform.SetParent(transform);
            headObj.transform.localScale = new Vector3(HEAD_SCALE, HEAD_HEIGHT_SCALE, HEAD_SCALE);
            _head = headObj.transform;
            _headRenderer = headObj.GetComponent<Renderer>();
            DestroyCollider(headObj);

            // Create left hand (sphere)
            var leftHandObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            leftHandObj.name = "LeftHand";
            leftHandObj.transform.SetParent(transform);
            leftHandObj.transform.localScale = Vector3.one * HAND_SCALE;
            _leftHand = leftHandObj.transform;
            _leftHandRenderer = leftHandObj.GetComponent<Renderer>();
            DestroyCollider(leftHandObj);

            // Create right hand (sphere)
            var rightHandObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rightHandObj.name = "RightHand";
            rightHandObj.transform.SetParent(transform);
            rightHandObj.transform.localScale = Vector3.one * HAND_SCALE;
            _rightHand = rightHandObj.transform;
            _rightHandRenderer = rightHandObj.GetComponent<Renderer>();
            DestroyCollider(rightHandObj);

            // Set player color
            SetPlayerColor(playerColor);
            
            // Create name tag above head
            CreateNameTag(_head);
            
            // Create muzzle flash effect
            CreateMuzzleFlash(_rightHand);
        }

        private void CreateNameTag(Transform parent)
        {
            var nameTagObj = new GameObject("NameTag");
            nameTagObj.transform.SetParent(parent);
            nameTagObj.transform.localPosition = new Vector3(0, NAME_TAG_HEIGHT_OFFSET, 0);
            
            _nameTag = nameTagObj.AddComponent<TextMesh>();
            _nameTag.text = PlayerName;
            _nameTag.fontSize = 24;
            _nameTag.characterSize = 0.02f;
            _nameTag.anchor = TextAnchor.MiddleCenter;
            _nameTag.alignment = TextAlignment.Center;
            _nameTag.color = Color.white;
            
            nameTagObj.AddComponent<BillboardText>();
        }
        
        private void CreateMuzzleFlash(Transform parent)
        {
            var flashObj = new GameObject("MuzzleFlash");
            flashObj.transform.SetParent(parent);
            flashObj.transform.localPosition = new Vector3(0, 0, MUZZLE_FLASH_OFFSET);
            
            _muzzleFlash = flashObj.AddComponent<ParticleSystem>();
            var main = _muzzleFlash.main;
            main.startLifetime = MUZZLE_FLASH_LIFETIME;
            main.startSpeed = 0f;
            main.startSize = MUZZLE_FLASH_SIZE;
            main.startColor = new Color(1f, 0.8f, 0.3f);
            main.maxParticles = 1;
            main.loop = false;
            main.playOnAwake = false;
            
            var emission = _muzzleFlash.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
            
            var shape = _muzzleFlash.shape;
            shape.enabled = false;
            
            var light = flashObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.8f, 0.3f);
            light.intensity = 0;
            light.range = MUZZLE_FLASH_LIGHT_RANGE;
        }

        private void DestroyCollider(GameObject obj)
        {
            var collider = obj.GetComponent<Collider>();
            if (collider) Destroy(collider);
        }

        private void Update()
        {
            if (IsDead) return;
            
            float t = Time.deltaTime * _interpolationSpeed;

            if (_usingRealModel)
            {
                UpdateRealModel(t);
                
                // Debug: Log comprehensive status periodically (every ~5 seconds)
                if (_statusLogCounter++ % 150 == 0 && _playerModel != null)
                {
                    Plugin.Log.LogInfo($"[STATUS] ========== {PlayerName} STATUS ==========");
                    Plugin.Log.LogInfo($"[STATUS] Model active: {_playerModel.activeSelf}");
                    Plugin.Log.LogInfo($"[STATUS] Model position: {_playerModel.transform.position}");
                    Plugin.Log.LogInfo($"[STATUS] Target root: {_targetRootPosition}");
                    Plugin.Log.LogInfo($"[STATUS] Target head: {_targetHeadPos}");
                    Plugin.Log.LogInfo($"[STATUS] Target LHand: {_targetLeftHandPos}");
                    Plugin.Log.LogInfo($"[STATUS] Target RHand: {_targetRightHandPos}");
                    Plugin.Log.LogInfo($"[STATUS] HeadTarget actual: {_headTarget?.position}");
                    Plugin.Log.LogInfo($"[STATUS] LHandTarget actual: {_leftHandTarget?.position}");
                    Plugin.Log.LogInfo($"[STATUS] RHandTarget actual: {_rightHandTarget?.position}");
                    Plugin.Log.LogInfo($"[STATUS] VRIK: {_vrik != null}, initiated={_vrik?.solver?.initiated}");
                    Plugin.Log.LogInfo($"[STATUS] Hand anim init: {_handAnimationInitialized}");
                    Plugin.Log.LogInfo($"[STATUS] Left grip: thumb={_leftThumbCurl:F2}, index={_leftIndexCurl:F2}");
                    Plugin.Log.LogInfo($"[STATUS] Right grip: thumb={_rightThumbCurl:F2}, index={_rightIndexCurl:F2}");
                    Plugin.Log.LogInfo($"[STATUS] Weapons: L={_leftWeaponId}, R={_rightWeaponId}");
                    
                    // Check renderers
                    var renderers = _playerModel.GetComponentsInChildren<Renderer>(true);
                    int enabledCount = 0;
                    foreach (var r in renderers) if (r.enabled) enabledCount++;
                    Plugin.Log.LogInfo($"[STATUS] Renderers: {enabledCount}/{renderers.Length} enabled");
                    Plugin.Log.LogInfo($"[STATUS] ==========================================");
                }
            }
            else
            {
                UpdatePrimitiveVisuals(t);
                
                // Log primitive status occasionally
                if (_statusLogCounter++ % 150 == 0)
                {
                    Plugin.Log.LogInfo($"[STATUS] {PlayerName} (PRIMITIVE): pos={transform.position}, head={_head?.position}, visible={gameObject.activeSelf}");
                }
            }
        }
        
        private int _statusLogCounter = 0;
        
        private void LateUpdate()
        {
            if (!_usingRealModel || _playerModel == null) return;
            
            // Fix head visibility AFTER HideLegs runs (HideLegs uses LateUpdate)
            // This ensures the head stays visible for remote players
            
            // Cache the transforms on first run
            if (_headTransformsToFix == null)
            {
                var headTransforms = new System.Collections.Generic.List<Transform>();
                var headRenderers = new System.Collections.Generic.List<SkinnedMeshRenderer>();
                
                foreach (var t in _playerModel.GetComponentsInChildren<Transform>(true))
                {
                    string nameLower = t.gameObject.name.ToLower();
                    // Include head, eyes, helmet, visor - anything that might be hidden
                    if (nameLower.Contains("head") || nameLower == "head" || nameLower.Contains("eye") ||
                        nameLower.Contains("helmet") || nameLower.Contains("visor"))
                    {
                        headTransforms.Add(t);
                        var smr = t.GetComponent<SkinnedMeshRenderer>();
                        if (smr != null) headRenderers.Add(smr);
                    }
                }
                
                _headTransformsToFix = headTransforms.ToArray();
                _headRenderersToFix = headRenderers.ToArray();
                
                Plugin.Log.LogInfo($"[HEAD FIX] Cached {_headTransformsToFix.Length} head transforms and {_headRenderersToFix.Length} head renderers for {PlayerName}");
            }
            
            // Fix head transforms every frame (in case something keeps hiding them)
            foreach (var t in _headTransformsToFix)
            {
                if (t == null) continue;
                
                // Reset scale if it was set to zero
                if (t.localScale == Vector3.zero || t.localScale.magnitude < 0.01f)
                {
                    t.localScale = Vector3.one;
                }
                
                // Ensure layer matches the visible body layer (same as torso - layer 11)
                if (t.gameObject.layer != _visibleBodyLayer)
                {
                    t.gameObject.layer = _visibleBodyLayer;
                }
            }
            
            // Ensure head renderers stay enabled and on correct layer
            foreach (var smr in _headRenderersToFix)
            {
                if (smr == null) continue;
                smr.enabled = true;
                if (smr.gameObject.layer != _visibleBodyLayer)
                {
                    smr.gameObject.layer = _visibleBodyLayer;
                }
            }
        }

        private void UpdateRealModel(float t)
        {
            // Interpolate IK targets to synced positions
            if (_headTarget != null)
            {
                _headTarget.position = Vector3.Lerp(_headTarget.position, _targetHeadPos, t);
                // Only update rotation if we have a valid rotation
                if (_targetHeadRot != Quaternion.identity || _headTarget.rotation == Quaternion.identity)
                {
                    _headTarget.rotation = Quaternion.Slerp(_headTarget.rotation, _targetHeadRot, t);
                }
            }
            
            if (_leftHandTarget != null)
            {
                _leftHandTarget.position = Vector3.Lerp(_leftHandTarget.position, _targetLeftHandPos, t);
                _leftHandTarget.rotation = Quaternion.Slerp(_leftHandTarget.rotation, _targetLeftHandRot, t);
            }
            
            if (_rightHandTarget != null)
            {
                _rightHandTarget.position = Vector3.Lerp(_rightHandTarget.position, _targetRightHandPos, t);
                _rightHandTarget.rotation = Quaternion.Slerp(_rightHandTarget.rotation, _targetRightHandRot, t);
            }
            
            // Update body root position - place it at the root position (floor level under head)
            if (_bodyRoot != null && _headTarget != null)
            {
                // Position body at root position (this is the player's feet position)
                Vector3 targetBodyPos = _targetRootPosition;
                _bodyRoot.position = Vector3.Lerp(_bodyRoot.position, targetBodyPos, t);
                
                // Rotate body to face head direction (yaw only)
                Vector3 headForward = _headTarget.forward;
                headForward.y = 0;
                if (headForward.sqrMagnitude > 0.01f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(headForward.normalized);
                    _bodyRoot.rotation = Quaternion.Slerp(_bodyRoot.rotation, targetRot, t * BODY_ROTATION_LERP_FACTOR);
                }
            }
            
            // Apply finger animation based on grip values
            ApplyGripToHands();
            
            // Debug: Log model state periodically
            if (_modelDebugCounter++ % 300 == 0)
            {
                Plugin.Log.LogInfo($"[MODEL UPDATE] {PlayerName} state:");
                Plugin.Log.LogInfo($"[MODEL UPDATE]   Model active: {_playerModel?.activeSelf}");
                Plugin.Log.LogInfo($"[MODEL UPDATE]   Body root pos: {_bodyRoot?.position}");
                Plugin.Log.LogInfo($"[MODEL UPDATE]   Head target pos: {_headTarget?.position}");
                Plugin.Log.LogInfo($"[MODEL UPDATE]   Target head pos: {_targetHeadPos}");
                Plugin.Log.LogInfo($"[MODEL UPDATE]   VRIK enabled: {_vrik?.enabled}, initiated: {_vrik?.solver?.initiated}");
            }
            
            // VRIK is now enabled and will update automatically via Unity's LateUpdate
        }
        
        private int _modelDebugCounter = 0;

        private void UpdatePrimitiveVisuals(float t)
        {
            // Interpolate root position
            transform.position = Vector3.Lerp(transform.position, _targetRootPosition, t);

            if (_head != null)
            {
                _head.position = Vector3.Lerp(_head.position, _targetHeadPos, t);
                _head.rotation = Quaternion.Slerp(_head.rotation, _targetHeadRot, t);
            }
            if (_leftHand != null)
            {
                _leftHand.position = Vector3.Lerp(_leftHand.position, _targetLeftHandPos, t);
                _leftHand.rotation = Quaternion.Slerp(_leftHand.rotation, _targetLeftHandRot, t);
            }
            if (_rightHand != null)
            {
                _rightHand.position = Vector3.Lerp(_rightHand.position, _targetRightHandPos, t);
                _rightHand.rotation = Quaternion.Slerp(_rightHand.rotation, _targetRightHandRot, t);
            }
            
            // Update body position to follow head horizontally
            if (_body != null && _head != null)
            {
                var bodyPos = new Vector3(_head.position.x, transform.position.y + 0.5f, _head.position.z);
                _body.position = Vector3.Lerp(_body.position, bodyPos, t);
                
                Vector3 headForward = _head.forward;
                headForward.y = 0;
                if (headForward.sqrMagnitude > 0.01f)
                {
                    _body.rotation = Quaternion.Slerp(_body.rotation, Quaternion.LookRotation(headForward), t);
                }
            }
        }

        #region Target Setters

        public void SetTargetPosition(Vector3 position)
        {
            _targetRootPosition = position;
            _lastUpdateTime = Time.time;
            
            // Check if this player is on a moving platform and register them
            if (WorldSync.Instance != null)
            {
                var platform = WorldSync.Instance.FindPlatformAtPosition(position);
                if (platform != null)
                {
                    WorldSync.Instance.RegisterRemotePlayerOnPlatform(SteamId, platform);
                }
                else
                {
                    WorldSync.Instance.UnregisterRemotePlayerFromPlatforms(SteamId);
                }
            }
            
            // Debug: Log position updates more frequently
            if (_setTargetLogCounter++ % SET_TARGET_LOG_INTERVAL == 0)
            {
                Plugin.Log.LogInfo($"[REMOTE] {PlayerName} SetTargetPosition: {position}");
            }
        }
        
        /// <summary>
        /// Apply platform delta to keep remote player moving with platform.
        /// Called from PlayerManager after all position updates.
        /// </summary>
        public void ApplyPlatformDelta()
        {
            if (WorldSync.Instance == null) return;
            
            Vector3 delta = WorldSync.Instance.GetPlatformDeltaForPlayer(SteamId);
            if (delta.sqrMagnitude > 0.0001f)
            {
                _targetRootPosition += delta;
                _targetHeadPos += delta;
                _targetLeftHandPos += delta;
                _targetRightHandPos += delta;
            }
        }

        public void SetTargetHeadTransform(Vector3 position, Quaternion rotation)
        {
            _targetHeadPos = position;
            _targetHeadRot = rotation;
            _lastUpdateTime = Time.time;
            
            // Debug: Log head updates more frequently
            if (_setTargetLogCounter % SET_TARGET_LOG_INTERVAL == 0)
            {
                Plugin.Log.LogInfo($"[REMOTE] {PlayerName} SetTargetHeadTransform: {position}");
            }
        }

        public void SetTargetLeftHandTransform(Vector3 position, Quaternion rotation)
        {
            _targetLeftHandPos = position;
            _targetLeftHandRot = rotation;
            _lastUpdateTime = Time.time;
            
            // Debug: Log hand updates occasionally
            if (UnityEngine.Random.value < 0.01f)
            {
                Plugin.Log.LogInfo($"[REMOTE] {PlayerName} left hand updated to {position}");
            }
        }

        public void SetTargetRightHandTransform(Vector3 position, Quaternion rotation)
        {
            _targetRightHandPos = position;
            _targetRightHandRot = rotation;
            _lastUpdateTime = Time.time;
            
            // Debug: Log hand updates occasionally
            if (UnityEngine.Random.value < 0.01f)
            {
                Plugin.Log.LogInfo($"[REMOTE] {PlayerName} right hand updated to {position}");
            }
        }

        #endregion

        #region Health & State

        public void SetHealth(float current, float max)
        {
            CurrentHealth = current;
            MaxHealth = max;
            
            if (!_usingRealModel)
            {
                float healthPercent = Mathf.Clamp01(current / max);
                Color baseColor = GetColorFromSteamId(SteamId);
                Color healthColor = Color.Lerp(Color.red, baseColor, healthPercent);
                SetPlayerColor(healthColor);
            }
        }

        public void OnDeath()
        {
            IsDead = true;
            if (!_usingRealModel)
            {
                SetAlpha(0.3f);
            }
        }

        public void OnRespawn(Vector3 position)
        {
            IsDead = false;
            _targetRootPosition = position;
            transform.position = position;
            CurrentHealth = MaxHealth;
            
            if (!_usingRealModel)
            {
                SetAlpha(1f);
                SetPlayerColor(GetColorFromSteamId(SteamId));
            }
        }

        #endregion

        #region Weapon & Scene

        public void SetWeapon(string weaponId)
        {
            CurrentWeapon = weaponId;
        }

        public void OnWeaponFire(string weaponId, Vector3 direction)
        {
            Plugin.Log.LogInfo($"[WEAPON VISUAL] {PlayerName} fired {weaponId}, showing muzzle flash");
            
            if (_muzzleFlash != null)
            {
                _muzzleFlash.Play();
                var light = _muzzleFlash.GetComponent<Light>();
                if (light != null)
                {
                    StartCoroutine(FlashLight(light));
                }
            }
            else
            {
                Plugin.Log.LogWarning($"[WEAPON VISUAL] {PlayerName} has no muzzle flash effect!");
            }
        }
        
        private System.Collections.IEnumerator FlashLight(Light light)
        {
            light.intensity = MUZZLE_FLASH_LIGHT_INTENSITY;
            yield return new WaitForSeconds(MUZZLE_FLASH_DURATION);
            light.intensity = 0f;
        }

        public void SetScene(string sceneName)
        {
            CurrentScene = sceneName;
            IsSceneReady = false;
        }

        public void SetSceneReady(string sceneName)
        {
            if (CurrentScene == sceneName)
            {
                IsSceneReady = true;
            }
        }

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }

        /// <summary>
        /// Set hand grip/finger curl values
        /// </summary>
        public void SetGrip(float lThumb, float lIndex, float lMiddle, float lRing, float lPinky,
                          float rThumb, float rIndex, float rMiddle, float rRing, float rPinky)
        {
            _leftThumbCurl = lThumb;
            _leftIndexCurl = lIndex;
            _leftMiddleCurl = lMiddle;
            _leftRingCurl = lRing;
            _leftPinkyCurl = lPinky;
            
            _rightThumbCurl = rThumb;
            _rightIndexCurl = rIndex;
            _rightMiddleCurl = rMiddle;
            _rightRingCurl = rRing;
            _rightPinkyCurl = rPinky;
            
            // Debug: Log grip values more frequently for debugging
            if (_gripLogCounter++ % 30 == 0)
            {
                Plugin.Log.LogInfo($"[GRIP RECV] {PlayerName} grip received:");
                Plugin.Log.LogInfo($"[GRIP RECV]   Left: thumb={lThumb:F2}, index={lIndex:F2}, middle={lMiddle:F2}, ring={lRing:F2}, pinky={lPinky:F2}");
                Plugin.Log.LogInfo($"[GRIP RECV]   Right: thumb={rThumb:F2}, index={rIndex:F2}, middle={rMiddle:F2}, ring={rRing:F2}, pinky={rPinky:F2}");
                Plugin.Log.LogInfo($"[GRIP RECV]   Hand anim initialized: {_handAnimationInitialized}");
                Plugin.Log.LogInfo($"[GRIP RECV]   Left bones: {(_leftHandBones != null ? CountNonNull(_leftHandBones) : 0)}, Right bones: {(_rightHandBones != null ? CountNonNull(_rightHandBones) : 0)}");
                Plugin.Log.LogInfo($"[GRIP RECV]   Left poses: open={_leftOpenBonePositions != null}, closed={_leftClosedBonePositions != null}");
                Plugin.Log.LogInfo($"[GRIP RECV]   Right poses: open={_rightOpenBonePositions != null}, closed={_rightClosedBonePositions != null}");
            }
            
            // Apply to hand animators if we have them
            ApplyGripToHands();
        }

        /// <summary>
        /// Apply grip values to hand bones
        /// </summary>
        private void ApplyGripToHands()
        {
            if (!_handAnimationInitialized) return;
            
            // Apply to left hand
            if (_leftHandBones != null && _leftHandBones.Length > 0 &&
                _leftOpenBonePositions != null && _leftClosedBonePositions != null)
            {
                ApplyGripToHandBones(_leftHandBones, 
                    _leftOpenBonePositions, _leftOpenBoneRotations,
                    _leftClosedBonePositions, _leftClosedBoneRotations,
                    _leftThumbCurl, _leftIndexCurl, _leftMiddleCurl, _leftRingCurl, _leftPinkyCurl);
            }
            
            // Apply to right hand
            if (_rightHandBones != null && _rightHandBones.Length > 0 &&
                _rightOpenBonePositions != null && _rightClosedBonePositions != null)
            {
                ApplyGripToHandBones(_rightHandBones,
                    _rightOpenBonePositions, _rightOpenBoneRotations,
                    _rightClosedBonePositions, _rightClosedBoneRotations,
                    _rightThumbCurl, _rightIndexCurl, _rightMiddleCurl, _rightRingCurl, _rightPinkyCurl);
            }
        }
        
        /// <summary>
        /// Apply grip values to a set of hand bones by blending between open/closed poses
        /// </summary>
        private void ApplyGripToHandBones(Transform[] bones,
            Vector3[] openPositions, Quaternion[] openRotations,
            Vector3[] closedPositions, Quaternion[] closedRotations,
            float thumbCurl, float indexCurl, float middleCurl, float ringCurl, float pinkyCurl)
        {
            if (bones == null || openPositions == null || closedPositions == null) return;
            
            int boneCount = Mathf.Min(bones.Length, openPositions.Length, closedPositions.Length);
            
            for (int i = 0; i < boneCount; i++)
            {
                if (bones[i] == null) continue;
                
                // Determine which finger this bone belongs to and get the curl value
                float curl = GetCurlForBone(i, thumbCurl, indexCurl, middleCurl, ringCurl, pinkyCurl);
                
                // Skip wrist (bone 0) - it's controlled by IK
                if (i == 0) continue;
                
                // Blend between open and closed pose based on curl
                bones[i].localPosition = Vector3.Lerp(openPositions[i], closedPositions[i], curl);
                bones[i].localRotation = Quaternion.Slerp(openRotations[i], closedRotations[i], curl);
            }
        }
        
        /// <summary>
        /// Get the curl value for a specific bone index based on SteamVR bone layout
        /// </summary>
        private float GetCurlForBone(int boneIndex, float thumb, float index, float middle, float ring, float pinky)
        {
            // SteamVR bone layout (25 bones):
            // 0: Wrist
            // 1-4: Thumb (metacarpal, proximal, middle, distal/tip)
            // 5-9: Index (metacarpal, proximal, middle, distal, tip)
            // 10-14: Middle
            // 15-19: Ring
            // 20-24: Pinky
            
            if (boneIndex >= 1 && boneIndex <= 4) return thumb;
            if (boneIndex >= 5 && boneIndex <= 9) return index;
            if (boneIndex >= 10 && boneIndex <= 14) return middle;
            if (boneIndex >= 15 && boneIndex <= 19) return ring;
            if (boneIndex >= 20 && boneIndex <= 24) return pinky;
            
            return 0f; // Wrist or unknown
        }

        /// <summary>
        /// Set equipped weapons for both hands
        /// </summary>
        public void SetEquippedWeapons(string leftWeaponId, string rightWeaponId)
        {
            Plugin.Log.LogInfo($"[WEAPON VISUAL] {PlayerName} SetEquippedWeapons: L={leftWeaponId}, R={rightWeaponId} (was L={_leftWeaponId}, R={_rightWeaponId})");
            
            if (_leftWeaponId != leftWeaponId)
            {
                _leftWeaponId = leftWeaponId;
                UpdateWeaponVisual(true, leftWeaponId);
            }
            
            if (_rightWeaponId != rightWeaponId)
            {
                _rightWeaponId = rightWeaponId;
                UpdateWeaponVisual(false, rightWeaponId);
            }
        }

        /// <summary>
        /// Update weapon visual for a hand
        /// </summary>
        private void UpdateWeaponVisual(bool isLeftHand, string weaponId)
        {
            string handName = isLeftHand ? "left" : "right";
            Plugin.Log.LogInfo($"[WEAPON VISUAL] {PlayerName} UpdateWeaponVisual: {handName} hand -> {weaponId}");
            
            // Destroy old weapon visual
            if (isLeftHand && _leftWeaponVisual != null)
            {
                Plugin.Log.LogInfo($"[WEAPON VISUAL] Destroying old left weapon visual");
                Destroy(_leftWeaponVisual);
                _leftWeaponVisual = null;
            }
            else if (!isLeftHand && _rightWeaponVisual != null)
            {
                Plugin.Log.LogInfo($"[WEAPON VISUAL] Destroying old right weapon visual");
                Destroy(_rightWeaponVisual);
                _rightWeaponVisual = null;
            }
            
            if (weaponId == "none" || string.IsNullOrEmpty(weaponId))
            {
                Plugin.Log.LogInfo($"[WEAPON VISUAL] No weapon to show for {handName} hand");
                return;
            }
            
            // Try to find the weapon prefab and create a visual
            try
            {
                var weaponVisual = CreateWeaponVisual(weaponId);
                if (weaponVisual != null)
                {
                    var handTarget = isLeftHand ? _leftHandTarget : _rightHandTarget;
                    if (handTarget != null)
                    {
                        weaponVisual.transform.SetParent(handTarget);
                        weaponVisual.transform.localPosition = Vector3.zero;
                        weaponVisual.transform.localRotation = Quaternion.identity;
                        
                        if (isLeftHand)
                            _leftWeaponVisual = weaponVisual;
                        else
                            _rightWeaponVisual = weaponVisual;
                        
                        Plugin.Log.LogInfo($"[WEAPON] Created weapon visual for {PlayerName}: {weaponId} in {(isLeftHand ? "left" : "right")} hand");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[WEAPON] Failed to create weapon visual for {weaponId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a visual representation of a weapon
        /// </summary>
        private GameObject CreateWeaponVisual(string weaponId)
        {
            Plugin.Log.LogInfo($"[WEAPON VISUAL] CreateWeaponVisual: searching for {weaponId}");
            
            // Try to find the weapon profile in the game's equippables
            if (VertigoPlayer.instance?.equippablesManager == null)
            {
                Plugin.Log.LogWarning($"[WEAPON VISUAL] No equippablesManager available");
                return null;
            }
            
            var equippables = VertigoPlayer.instance.equippablesManager;
            
            // Log all available equippables for debugging
            Plugin.Log.LogInfo($"[WEAPON VISUAL] Searching {equippables.allEquippables?.Length ?? 0} equippables for '{weaponId}'");
            
            // Search through all equippables to find matching weapon
            foreach (var profile in equippables.allEquippables)
            {
                if (profile != null && profile.name == weaponId)
                {
                    Plugin.Log.LogInfo($"[WEAPON VISUAL] Found matching profile: {profile.name}, prefab={profile.prefab != null}");
                    
                    // Found the weapon profile, try to instantiate its visual
                    if (profile.prefab != null)
                    {
                        var visual = Instantiate(profile.prefab);
                        visual.name = $"WeaponVisual_{weaponId}";
                        
                        // Disable any scripts that would cause issues
                        DisableWeaponScripts(visual);
                        
                        Plugin.Log.LogInfo($"[WEAPON VISUAL] Created visual from prefab for {weaponId}");
                        return visual;
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[WEAPON VISUAL] Profile {profile.name} has no prefab!");
                    }
                }
            }
            
            Plugin.Log.LogWarning($"[WEAPON VISUAL] No matching profile found for '{weaponId}', creating placeholder");
            // Weapon not found, create a simple placeholder
            return CreatePlaceholderWeapon(weaponId);
        }

        /// <summary>
        /// Disable scripts on weapon visual that would cause issues
        /// </summary>
        private void DisableWeaponScripts(GameObject weapon)
        {
            // Disable all MonoBehaviours except renderers and transforms
            foreach (var comp in weapon.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp != null)
                {
                    comp.enabled = false;
                }
            }
            
            // Disable colliders
            foreach (var col in weapon.GetComponentsInChildren<Collider>(true))
            {
                col.enabled = false;
            }
            
            // Disable rigidbodies
            foreach (var rb in weapon.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
            }
        }

        /// <summary>
        /// Create a simple placeholder weapon visual
        /// </summary>
        private GameObject CreatePlaceholderWeapon(string weaponId)
        {
            // Create a simple cube as placeholder
            var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            placeholder.name = $"WeaponPlaceholder_{weaponId}";
            placeholder.transform.localScale = new Vector3(0.05f, 0.05f, 0.2f);
            
            // Remove collider
            var col = placeholder.GetComponent<Collider>();
            if (col != null) Destroy(col);
            
            // Set color based on weapon name hash
            var renderer = placeholder.GetComponent<Renderer>();
            if (renderer != null)
            {
                int hash = weaponId.GetHashCode();
                renderer.material.color = Color.HSVToRGB(Mathf.Abs(hash % 360) / 360f, 0.5f, 0.8f);
            }
            
            return placeholder;
        }

        public void SetCharacter(string characterId)
        {
            if (CharacterId == characterId) return;
            
            CharacterId = characterId;
            
            var character = FindCharacterById(characterId);
            if (character == null) return;
            
            // Clean up existing model
            CleanupModel();
            
            // Try to create the new character model
            if (!TryCreatePlayerModel(character))
            {
                CreatePrimitiveVisuals();
            }
        }
        
        private void CleanupModel()
        {
            if (_playerModel != null)
            {
                Destroy(_playerModel);
                _playerModel = null;
            }
            
            // Clean up IK targets
            if (_headTarget != null) Destroy(_headTarget.parent.gameObject);
            _headTarget = null;
            _leftHandTarget = null;
            _rightHandTarget = null;
            
            // Clean up primitives
            if (_head != null) Destroy(_head.gameObject);
            if (_leftHand != null) Destroy(_leftHand.gameObject);
            if (_rightHand != null) Destroy(_rightHand.gameObject);
            if (_body != null) Destroy(_body.gameObject);
            _head = null;
            _leftHand = null;
            _rightHand = null;
            _body = null;
            
            _vrik = null;
            _bodyRoot = null;
            _usingRealModel = false;
            _headTransformsToFix = null;
            _headRenderersToFix = null;
            
            // Reset hand animation state
            _leftHandAnimator = null;
            _rightHandAnimator = null;
            _leftHandBones = null;
            _rightHandBones = null;
            _leftOpenBonePositions = null;
            _leftOpenBoneRotations = null;
            _leftClosedBonePositions = null;
            _leftClosedBoneRotations = null;
            _rightOpenBonePositions = null;
            _rightOpenBoneRotations = null;
            _rightClosedBonePositions = null;
            _rightClosedBoneRotations = null;
            _handAnimationInitialized = false;
        }
        
        private PlayableCharacter FindCharacterById(string id)
        {
            if (GameManager.instance == null) return null;
            
            foreach (var character in GameManager.instance.playableCharacters)
            {
                if (character != null && character.id == id)
                {
                    return character;
                }
            }
            return null;
        }

        #endregion

        #region Visuals

        private void SetPlayerColor(Color color)
        {
            if (_headRenderer) _headRenderer.material.color = color;
            if (_leftHandRenderer) _leftHandRenderer.material.color = color;
            if (_rightHandRenderer) _rightHandRenderer.material.color = color;
            if (_bodyRenderer) _bodyRenderer.material.color = Color.Lerp(color, Color.gray, 0.3f);
        }

        private void SetAlpha(float alpha)
        {
            SetRendererAlpha(_headRenderer, alpha);
            SetRendererAlpha(_leftHandRenderer, alpha);
            SetRendererAlpha(_rightHandRenderer, alpha);
            SetRendererAlpha(_bodyRenderer, alpha);
        }

        private void SetRendererAlpha(Renderer renderer, float alpha)
        {
            if (renderer == null) return;
            var color = renderer.material.color;
            color.a = alpha;
            renderer.material.color = color;
        }

        private Color GetColorFromSteamId(CSteamID steamId)
        {
            int hash = steamId.GetHashCode();
            return Color.HSVToRGB(Mathf.Abs(hash % 360) / 360f, 0.7f, 0.9f);
        }

        #endregion
        
        #region Public Accessors for EnemySync
        
        /// <summary>
        /// Get the root transform of the player model (for enemy targeting)
        /// </summary>
        public Transform GetModelRoot()
        {
            if (_usingRealModel && _bodyRoot != null)
                return _bodyRoot;
            return transform;
        }
        
        /// <summary>
        /// Get the head target transform (for enemy targeting)
        /// </summary>
        public Transform GetHeadTarget()
        {
            if (_usingRealModel && _headTarget != null)
                return _headTarget;
            if (_head != null)
                return _head;
            return transform;
        }
        
        #endregion
        
        private void OnDestroy()
        {
            CleanupModel();
        }
    }

    /// <summary>
    /// Makes the name tag always face the camera
    /// </summary>
    public class BillboardText : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                transform.LookAt(transform.position + cam.transform.rotation * Vector3.forward,
                                 cam.transform.rotation * Vector3.up);
            }
        }
    }
}
