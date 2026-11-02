using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class FPSLimiter : MonoBehaviour
{
	[Range(15f, 165f)]
	public int targetFPS = 60;

	[Header("UI References")]
	public Slider fpsSlider;

	public TextMeshProUGUI fpsLabel;

	private int previousFPS;
	public int EffectiveFPS { get; private set; }
	public string RuntimePolicy { get; private set; } = "requested";
	private float lastActivity;

	private void Start()
	{
		targetFPS = PlayerPrefs.GetInt("FPSLimit", targetFPS);
		if ((bool)fpsSlider)
		{
			fpsSlider.minValue = 15f;
			fpsSlider.maxValue = 165f;
			fpsSlider.value = targetFPS;
			fpsSlider.onValueChanged.AddListener(SetFPSLimit);
		}
		ApplyFPSLimit();
		UpdateFPSLabel(targetFPS);
	}

	private void Update()
	{
		if (targetFPS != previousFPS)
		{
			ApplyFPSLimit();
		}
		ApplyEffectiveLimit();
	}

	public void ApplyFPSLimit()
	{
		QualitySettings.vSyncCount = 0;
		ApplyEffectiveLimit();
		previousFPS = targetFPS;
		PlayerPrefs.SetInt("FPSLimit", targetFPS);
		PlayerPrefs.Save();
		UpdateFPSLabel(targetFPS);
		Debug.Log("FPS set to: " + targetFPS);
	}

	private void ApplyEffectiveLimit()
	{
		var manager = WindowManager.Instance?.CompanionManager;
		bool active = manager != null && manager.InteractionActive;
		if (active) lastActivity = Time.unscaledTime;
		bool hidden = manager != null && manager.ConfirmedHidden;
		int next = APIs.Hyprland.CompanionPresencePolicy.EffectiveFrames(targetFPS, manager != null,
			active, hidden, Time.unscaledTime, lastActivity);
		RuntimePolicy = manager == null ? "requested" : active || Time.unscaledTime - lastActivity < 2f ? "active" :
			hidden ? "hidden" : manager.ObservationFresh ? "passive" : "visibility-unknown";
		if (next == EffectiveFPS) return;
		EffectiveFPS = next;
		Application.targetFrameRate = next;
	}

	public void SetFPSLimit(float fps)
	{
		targetFPS = Mathf.RoundToInt(Mathf.Clamp(fps, 15f, 165f));
		ApplyFPSLimit();
	}

	private void UpdateFPSLabel(int fpsValue)
	{
		if ((bool)fpsLabel)
		{
			fpsLabel.text = $"{fpsValue}";
		}
	}
}
