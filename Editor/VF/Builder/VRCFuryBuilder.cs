using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VF.Feature.Base;
using VF.Inspector;
using VF.Model;
using VF.Model.Feature;
using Object = UnityEngine.Object;

namespace VF.Builder {

public class VRCFuryBuilder {
    public void TestRun(GameObject originalObject) {
        if (originalObject.name.StartsWith("VRCF ")) {
            EditorUtility.DisplayDialog("VRCFury Error", "This object is already the output of a VRCF test build.", "Ok");
            return;
        }
        var cloneName = "VRCF Test Build for " + originalObject.name;
        var exists = originalObject.scene
            .GetRootGameObjects()
            .FirstOrDefault(o => o.name == cloneName);
        if (exists) {
            Object.DestroyImmediate(exists);
        }
        var clone = Object.Instantiate(originalObject);
        if (clone.scene != originalObject.scene) {
            SceneManager.MoveGameObjectToScene(clone, originalObject.scene);
        }
        clone.name = cloneName;
        var result = SafeRun(originalObject, clone);
        if (result) {
            Selection.SetActiveObjectWithContext(clone, clone);
        } else {
            Object.DestroyImmediate(clone);
        }
    }
    
    public bool SafeRun(GameObject originalObject, GameObject avatarObject) {
        Debug.Log("VRCFury invoked on " + avatarObject.name + " ...");

        if (avatarObject.GetComponentsInChildren<VRCFury>(true).Length == 0) {
            Debug.Log("VRCFury components not found in avatar. Skipping.");
            return true;
        }

        var result = true;
        try {
            Run(originalObject, avatarObject);
        } catch(Exception e) {
            result = false;
            Debug.LogException(e);
            while (e is TargetInvocationException) {
                e = (e as TargetInvocationException).InnerException;
            }
            EditorUtility.DisplayDialog("VRCFury Error", "VRCFury encountered an error.\n\n" + e.Message, "Ok");
        }

        AssetDatabase.SaveAssets();
        EditorUtility.ClearProgressBar();
        return result;
    }

    private void Run(GameObject originalObject, GameObject avatarObject) {
        var progress = new ProgressBar("VRCFury is building ...");

        // Unhook everything from our assets before we delete them
        progress.Progress(0, "Cleaning up original (in case of old builds)");
        DetachFromAvatar(originalObject);
        
        progress.Progress(0.5, "Cleaning up clone (in case of old builds)");
        DetachFromAvatar(avatarObject);

        // Nuke all our old generated assets
        progress.Progress(0.1, "Clearing generated assets");
        var avatarPath = avatarObject.scene.path;
        if (string.IsNullOrEmpty(avatarPath)) {
            throw new Exception("Failed to find file path to avatar scene");
        }
        var tmpDir = "Assets/_VRCFury/" + VRCFuryEditorUtils.MakeFilenameSafe(originalObject.name);
        if (Directory.Exists(tmpDir)) {
            foreach (var asset in AssetDatabase.FindAssets("", new[] { tmpDir })) {
                var path = AssetDatabase.GUIDToAssetPath(asset);
                AssetDatabase.DeleteAsset(path);
            }
        }
        Directory.CreateDirectory(tmpDir);

        // Figure out what assets we're going to be messing with
        var avatar = avatarObject.GetComponent<VRCAvatarDescriptor>();
        var fxController = GetOrCreateAvatarFx(avatar, tmpDir, originalObject.name);
        var menu = GetOrCreateAvatarMenu(avatar, tmpDir, originalObject.name);
        var syncedParams = GetOrCreateAvatarParams(avatar, tmpDir, originalObject.name);

        // Attach our assets back to the avatar
        progress.Progress(0.2, "Attaching to avatar");
        AttachToAvatar(avatarObject, fxController, menu, syncedParams);

        progress.Progress(0.25, "Joining Menus");
        MenuSplitter.JoinMenus(menu);

        // Apply configs
        var menuManager = new MenuManager(menu, tmpDir);
        var paramsManager = new ParamManager(syncedParams);
        var controllerManager = new ControllerManager(fxController, tmpDir, paramsManager, VRCAvatarDescriptor.AnimLayerType.FX);
        var motions = new ClipBuilder(avatarObject);
        var defaultClip = controllerManager.NewClip("Defaults");
        ApplyFuryConfigs(
            controllerManager,
            menuManager,
            paramsManager,
            motions,
            tmpDir,
            defaultClip,
            avatarObject,
            progress.Partial(0.3, 0.8),
            out var forceWriteDefaultsOff
        );
        
        progress.Progress(0.8, "Splitting Menus");
        MenuSplitter.SplitMenus(menu);

        if (forceWriteDefaultsOff) {
            progress.Progress(0.85, "Creating Write Defaults Fix Copy");

            AddDefaultsLayer(controllerManager, defaultClip, avatarObject, true);
            
            foreach (var layer in fxController.layers) {
                DefaultClipBuilder.ForEachState(layer, state => {
                    state.writeDefaultValues = false;
                    if (state.motion == null) state.motion = controllerManager.GetNoopClip();
                });
            }
        } else {
            progress.Progress(0.85, "Collecting default states");
            AddDefaultsLayer(controllerManager, defaultClip, avatarObject, false);

            progress.Progress(0.9, "Adjusting 'Write Defaults'");
            UseWriteDefaultsIfNeeded(controllerManager);
        }
        
        progress.Progress(0.95, "Removing Junk Components");
        foreach (var c in avatarObject.GetComponentsInChildren<Animator>(true)) {
            if (c.gameObject != avatarObject && PrefabUtility.IsPartOfPrefabInstance(c.gameObject)) {
                Object.DestroyImmediate(c);
            }
        }

        foreach (var c in avatarObject.GetComponentsInChildren<VRCFury>(true)) {
            Object.DestroyImmediate(c);
        }
        foreach (var c in avatarObject.GetComponentsInChildren<Animator>(true)) {
            if (c.gameObject != avatarObject) Object.DestroyImmediate(c);
        }

        progress.Progress(1, "Finishing Up");
        EditorUtility.SetDirty(fxController);
        EditorUtility.SetDirty(menu);
        EditorUtility.SetDirty(syncedParams);

        Debug.Log("VRCFury Finished!");
    }

    private static void ApplyFuryConfigs(
        ControllerManager controller,
        MenuManager menu,
        ParamManager prms,
        ClipBuilder motions,
        string tmpDir,
        AnimationClip defaultClip,
        GameObject avatarObject,
        ProgressBar progress,
        out bool forceWriteDefaultsOff
    ) {
        var actions = new List<FeatureBuilderAction>();
        var totalActionCount = 0;
        var totalModelCount = 0;
        var collectedFeatures = new List<FeatureModel>();

        void AddModel(FeatureModel model, GameObject configObject) {
            collectedFeatures.Add(model);
            var isProp = configObject != avatarObject;
            var builder = FeatureFinder.GetBuilder(model, isProp);
            builder.featureBaseObject = configObject;
            builder.tmpDir = tmpDir;
            builder.addOtherFeature = m => AddModel(m, configObject);
            builder.uniqueModelNum = ++totalModelCount;
            builder.allFeaturesInRun = collectedFeatures;
            var builderActions = builder.GetActions();
            actions.AddRange(builderActions);
            totalActionCount += builderActions.Count;
        }

        progress.Progress(0, "Collecting features");
        foreach (var vrcFury in avatarObject.GetComponentsInChildren<VRCFury>(true)) {
            var configObject = vrcFury.gameObject;
            var config = vrcFury.config;
            if (config.features != null) {
                Debug.Log("Importing " + config.features.Count + " features from " + configObject.name);
                foreach (var feature in config.features) {
                    AddModel(feature, configObject);
                }
            }
        }
        
        while (actions.Count > 0) {
            var action = actions.Min();
            actions.Remove(action);
            var builder = action.GetBuilder();
            var configPath = AnimationUtility.CalculateTransformPath(builder.featureBaseObject.transform,
                avatarObject.transform);

            builder.controller = controller;
            builder.menu = menu;
            builder.prms = prms;
            builder.motions = motions;
            builder.defaultClip = defaultClip;
            builder.avatarObject = avatarObject;
            
            var statusMessage = "Applying " + action.GetName() + " on " + builder.avatarObject.name + " " + configPath;
            progress.Progress(1 - (actions.Count / (float)totalActionCount), statusMessage);

            action.Call();
        }

        forceWriteDefaultsOff = collectedFeatures
            .Any(feature => feature is MakeWriteDefaultsOff);
    }

    private static AnimatorController GetOrCreateAvatarFx(VRCAvatarDescriptor avatar, string tmpDir, string avatarName) {
        var origFx = VRCAvatarUtils.GetAvatarFx(avatar);
        var newPath = tmpDir + "/VRCFury for " + VRCFuryEditorUtils.MakeFilenameSafe(avatarName) + ".controller";
        if (origFx == null) {
            return AnimatorController.CreateAnimatorControllerAtPath(newPath);
        }
        AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(origFx), newPath);
        return AssetDatabase.LoadAssetAtPath<AnimatorController>(newPath);
    }

    private static VRCExpressionsMenu GetOrCreateAvatarMenu(VRCAvatarDescriptor avatar, string tmpDir, string avatarName) {
        var origMenu = VRCAvatarUtils.GetAvatarMenu(avatar);
        var newPath = tmpDir + "/VRCFury Menu for " + VRCFuryEditorUtils.MakeFilenameSafe(avatarName) + ".asset";
        var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        AssetDatabase.CreateAsset(menu, newPath);
        if (origMenu != null) {
            var menuManager = new MenuManager(menu, tmpDir);
            menuManager.MergeMenu(origMenu);
        }
        return menu;
    }

    private static VRCExpressionParameters GetOrCreateAvatarParams(VRCAvatarDescriptor avatar, string tmpDir, string avatarName) {
        var origParams = VRCAvatarUtils.GetAvatarParams(avatar);
        var newPath = tmpDir + "/VRCFury Params for " + VRCFuryEditorUtils.MakeFilenameSafe(avatarName) + ".asset";
        if (origParams == null) {
            var prms = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            prms.parameters = new VRCExpressionParameters.Parameter[]{};
            AssetDatabase.CreateAsset(prms, newPath);
            return prms;
        }
        AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(origParams), newPath);
        return AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(newPath);
    }

    public static void DetachFromAvatar(GameObject avatarObject) {
        var animator = avatarObject.GetComponent<Animator>();
        if (animator != null) {
            if (IsVrcfAsset(animator.runtimeAnimatorController)) {
                animator.runtimeAnimatorController = null;
            }
        }

        var avatar = avatarObject.GetComponent<VRCAvatarDescriptor>();

        var fx = VRCAvatarUtils.GetAvatarFx(avatar);
        if (IsVrcfAsset(fx)) {
            VRCAvatarUtils.SetAvatarFx(avatar, null);
        } else if (fx != null) {
            ControllerManager.PurgeFromAnimator(fx, VRCAvatarDescriptor.AnimLayerType.FX);
        }

        var menu = VRCAvatarUtils.GetAvatarMenu(avatar);
        if (IsVrcfAsset(menu)) {
            VRCAvatarUtils.SetAvatarMenu(avatar, null);
        } else if (menu != null) {
            MenuSplitter.JoinMenus(menu);
            MenuManager.PurgeFromMenu(menu);
            MenuSplitter.SplitMenus(menu);
        }

        var prms = VRCAvatarUtils.GetAvatarParams(avatar);
        if (IsVrcfAsset(prms)) {
            VRCAvatarUtils.SetAvatarParams(avatar, null);
        } else if (prms != null) {
            ParamManager.PurgeFromParams(prms);
        }

        EditorUtility.SetDirty(avatar);
    }

    private static void AttachToAvatar(GameObject avatarObject, AnimatorController fx, VRCExpressionsMenu menu, VRCExpressionParameters prms) {
        var avatar = avatarObject.GetComponent<VRCAvatarDescriptor>();
        var animator = avatarObject.GetComponent<Animator>();

        VRCAvatarUtils.SetAvatarFx(avatar, fx);
        if (animator != null) animator.runtimeAnimatorController = fx;
        avatar.customExpressions = true;
        avatar.expressionsMenu = menu;
        avatar.expressionParameters = prms;

        EditorUtility.SetDirty(avatar);
    }

    public static bool IsVrcfAsset(Object obj) {
        return obj != null && AssetDatabase.GetAssetPath(obj).Contains("_VRCFury");
    }

    private static void AddDefaultsLayer(ControllerManager manager, AnimationClip defaultClip, GameObject avatarObject, bool applyToUnmanagedLayers) {
        var defaultLayer = manager.NewLayer("Defaults", true);
        defaultLayer.NewState("Defaults").WithAnimation(defaultClip);
        foreach (var layer in manager.GetManagedLayers()) {
            DefaultClipBuilder.CollectDefaults(layer, defaultClip, avatarObject);
        }
        if (applyToUnmanagedLayers) {
            foreach (var layer in manager.GetUnmanagedLayers()) {
                DefaultClipBuilder.CollectDefaults(layer, defaultClip, avatarObject);
            }
        }
    }
    
    private static void UseWriteDefaultsIfNeeded(ControllerManager manager) {
        var offStates = 0;
        var onStates = 0;
        foreach (var layer in manager.GetUnmanagedLayers()) {
            DefaultClipBuilder.ForEachState(layer, state => {
                if (state.writeDefaultValues) onStates++;
                else offStates++;
            });
        }

        if (onStates > 0 && offStates > 0) {
            var weirdStates = new List<string>();
            var weirdAreOn = offStates > onStates;
            foreach (var layer in manager.GetUnmanagedLayers()) {
                DefaultClipBuilder.ForEachState(layer, state => {
                    if (state.writeDefaultValues == weirdAreOn) {
                        weirdStates.Add(layer.name+"."+state.name);
                    }
                });
            }
            Debug.LogWarning("Your animation controller contains a mix of Write Defaults ON and Write Defaults OFF states." +
                           " (" + onStates + " on, " + offStates + " off)." +
                           " Doing this may cause weird issues to happen with your animations in game." +
                           " This is not an issue with VRCFury, but an issue with your avatar's custom animation controller.");
            Debug.LogWarning("The broken states are most likely: " + String.Join(",", weirdStates));
        }
        
        // If half of the old states use writeDefaults, safe to assume it should be used everywhere
        var shouldUseWriteDefaults = onStates >= offStates && onStates > 0;
        if (shouldUseWriteDefaults) {
            Debug.Log("Detected usage of 'Write Defaults', adjusting generated states to use it too.");
            foreach (var layer in manager.GetManagedLayers()) {
                DefaultClipBuilder.ForEachState(layer, state => state.writeDefaultValues = true);
            }
        }
    }
}

}
