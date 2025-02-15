using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;


namespace Sinnwrig.FogVolumes.Editor
{
    [CustomEditor(typeof(FogVolumeProfile))]
    public class FogVolumeProfileEditor : UnityEditor.Editor
    {
        static class Styles
        {
            public static readonly GUIContent ambientColor = EditorGUIUtility.TrTextContent("Ambient Color", "The ambient color of the fog when not lit.");
            public static readonly GUIContent ambientOpacity = EditorGUIUtility.TrTextContent("Ambient Opacity", "The opacity of the ambient fog. A value of 0 will be only additive. Values higher than 0 will allow fog to influence background color based on density. ");

            public static readonly GUIContent albedo = EditorGUIUtility.TrTextContent("Albedo", "The base (multiplicative) color of the fog.");

            public static readonly GUIContent minMaxStepLength = EditorGUIUtility.TrTextContent("Step Size Range", "The minimum and maximum step lengths of the ray being marched. Lower values produce higher quality results. Higher values produce results with more banding.");
            public static readonly GUIContent stepIncrementFactor = EditorGUIUtility.TrTextContent("Step Increment", "The factor at which the step length taken by the raymarcher is increased.");
            public static readonly GUIContent maxRayLength = EditorGUIUtility.TrTextContent("Max Ray Length", "The maximum total length of a ray.");

            public static readonly GUIContent maxSampleCount = EditorGUIUtility.TrTextContent("Sample Count", "The maximum amount of samples or steps the raymarcher is allowed to take.");
            public static readonly GUIContent jitterStrength = EditorGUIUtility.TrTextContent("Jitter Strength", "The intensity of pseudorandom jitter used to offset the starting point of the raymarcher.");

            public static readonly GUIContent lightingMode = EditorGUIUtility.TrTextContent("Lighting Mode", "How scene lights will affect fog. When set to None, lights will not influence the fog. When set to Lit, lights will influence fog. When set to Shadowed, lights and shadows will influence the fog.");
            public static readonly GUIContent lightIntensityModifier = EditorGUIUtility.TrTextContent("Light Intensity Modifier", "Modulates the strength and intensity of the lights affecting the fog.");
            public static readonly GUIContent scattering = EditorGUIUtility.TrTextContent("Scattering", "How much light is scattered towards the camera. Higher values will produce brighter fog.");
            public static readonly GUIContent extinction = EditorGUIUtility.TrTextContent("Extinction", "How quickly light power is reduced based on its distance to the camera. Higher values will fade fog further away.");
            public static readonly GUIContent mieG = EditorGUIUtility.TrTextContent("Mie G", "Determines the distribution of scattered light based on the viewing angle. Higher values will increase how strongly brightness is focused on light position.");
            public static readonly GUIContent brightnessClamp = EditorGUIUtility.TrTextContent("Brightness Clamp", "The value at which the brightness of the fog will be clamped.");

            public static readonly GUIContent noiseTexture = EditorGUIUtility.TrTextContent("Noise Texture", "The world-space noise to apply to the fog volume.");
            public static readonly GUIContent scale = EditorGUIUtility.TrTextContent("Scale", "The scale of the noise texture.");
            public static readonly GUIContent noiseScroll = EditorGUIUtility.TrTextContent("Noise Scroll", "The direction in which the noise should scroll.");
            public static readonly GUIContent noiseIntensity = EditorGUIUtility.TrTextContent("Noise Intensity", "How intensely the noise will affect the fog.");
            public static readonly GUIContent intensityOffset = EditorGUIUtility.TrTextContent("Intensity Offset", "The offset applied to the noise texture values.");
        }

        private SerializedProperty albedo;

        private SerializedProperty ambientColor;
        private SerializedProperty ambientOpacity;

        private SerializedProperty minMaxStepLength;
        private SerializedProperty stepIncrementFactor;
        private SerializedProperty maxRayLength;

        private SerializedProperty maxSampleCount;
        private SerializedProperty jitterStrength;

        private SerializedProperty lightingMode;
        private SerializedProperty lightIntensityModifier;
        private SerializedProperty scattering;
        private SerializedProperty extinction;
        private SerializedProperty mieG;  
        private SerializedProperty brightnessClamp;

        private SerializedProperty noiseTexture;
        private SerializedProperty scale;
        private SerializedProperty noiseScroll;
        private SerializedProperty noiseIntensity;
        private SerializedProperty intensityOffset;

        static readonly string[] lightingOptions = new string[]
        {
            "No Lighting",
            "Has Lighting",
            "Has Shadows"
        };


        private void OnEnable()
        {
            PropertyFetcher<FogVolume> fetcher = new(serializedObject);

            albedo = fetcher.Find("albedo");

            ambientColor = fetcher.Find("ambientColor");
            ambientOpacity = fetcher.Find("ambientOpacity");

            minMaxStepLength = fetcher.Find("minMaxStepLength");
            stepIncrementFactor = fetcher.Find("stepIncrementFactor");
            maxRayLength = fetcher.Find("maxRayLength");

            maxSampleCount = fetcher.Find("maxSampleCount");
            jitterStrength = fetcher.Find("jitterStrength");

            lightingMode = fetcher.Find("lightingMode");
            lightIntensityModifier = fetcher.Find("lightIntensityModifier");
            scattering = fetcher.Find("scattering");
            extinction = fetcher.Find("extinction");

            mieG = fetcher.Find("mieG");
            brightnessClamp = fetcher.Find("brightnessClamp");

            noiseTexture = fetcher.Find("noiseTexture");
            scale = fetcher.Find("scale");
            noiseScroll = fetcher.Find("noiseScroll");
            noiseIntensity = fetcher.Find("noiseIntensity");
            intensityOffset = fetcher.Find("intensityOffset");
        }


        public override void OnInspectorGUI()
        {
            FogVolumeProfile actualTarget = (FogVolumeProfile)target;
            serializedObject.Update();

            using var scope = new EditorGUI.ChangeCheckScope();

            EditorGUILayout.LabelField("Appearance", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(albedo, Styles.albedo);
            EditorGUILayout.PropertyField(ambientColor, Styles.ambientColor);
            EditorGUILayout.PropertyField(ambientOpacity, Styles.ambientOpacity);

            EditorGUILayout.Space(EditorGUIUtility.singleLineHeight * 0.5f);
            EditorGUILayout.LabelField("Ray-Marching", EditorStyles.boldLabel);

            MinMaxProperty(0, 10, minMaxStepLength, Styles.minMaxStepLength);

            EditorGUILayout.PropertyField(stepIncrementFactor, Styles.stepIncrementFactor);
            EditorGUILayout.PropertyField(maxRayLength, Styles.maxRayLength);

            EditorGUILayout.PropertyField(maxSampleCount, Styles.maxSampleCount);
            EditorGUILayout.PropertyField(jitterStrength, Styles.jitterStrength);

            EditorGUILayout.Space(EditorGUIUtility.singleLineHeight * 0.5f);
            EditorGUILayout.LabelField("Lighting", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(lightingMode, Styles.lightingMode);

            EditorGUILayout.PropertyField(lightIntensityModifier, Styles.lightIntensityModifier);
            EditorGUILayout.PropertyField(scattering, Styles.scattering);
            EditorGUILayout.PropertyField(extinction, Styles.extinction);

            EditorGUILayout.PropertyField(mieG, Styles.mieG);
            EditorGUILayout.PropertyField(brightnessClamp, Styles.brightnessClamp);

            EditorGUILayout.Space(EditorGUIUtility.singleLineHeight * 0.5f);
            EditorGUILayout.LabelField("Noise", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(noiseTexture, Styles.noiseTexture);

            if (noiseTexture.objectReferenceValue != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(scale, Styles.scale);
                EditorGUILayout.PropertyField(noiseScroll, Styles.noiseScroll);
                EditorGUILayout.PropertyField(noiseIntensity, Styles.noiseIntensity);
                EditorGUILayout.PropertyField(intensityOffset, Styles.intensityOffset);
                EditorGUI.indentLevel--;
            }

            if (scope.changed)
                serializedObject.ApplyModifiedProperties();
        }


        private static void MinMaxProperty(float min, float max, SerializedProperty property, GUIContent label, params GUILayoutOption[] options)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);

            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            var v2Val = property.vector2Value;

            v2Val.x = EditorGUILayout.DelayedFloatField(v2Val.x, GUILayout.Width(50f));
            EditorGUILayout.MinMaxSlider(ref v2Val.x, ref v2Val.y, min, max);
            v2Val.y = EditorGUILayout.DelayedFloatField(v2Val.y, GUILayout.Width(50f));

            property.vector2Value = v2Val;

            EditorGUI.showMixedValue = false;
            EditorGUILayout.EndHorizontal();
        }
    }
}