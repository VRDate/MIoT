﻿//#define GPIO

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Devices.Gpio;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

using Windows.Devices.Enumeration;
using Microsoft.Maker.RemoteWiring;
using Microsoft.Maker.Serial;
using Waher.Content;
using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.BitsOfBinary;
using Waher.Networking.XMPP.Chat;
using Waher.Networking.XMPP.Control;
using Waher.Networking.XMPP.Sensor;
using Waher.Things;
using Waher.Things.ControlParameters;
using Waher.Things.SensorData;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Persistence.Filters;
using Waher.Runtime.Settings;
using Waher.Runtime.Inventory;

namespace ActuatorXmpp
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	sealed partial class App : Application
	{
		private static App instance = null;

#if GPIO
		private const int gpioOutputPin = 5;
		private GpioController gpio = null;
		private GpioPin gpioPin = null;
#else
		private UsbSerial arduinoUsb = null;
		private RemoteDevice arduino = null;
#endif
		private string deviceId;
		private XmppClient xmppClient = null;
		private ControlServer controlServer = null;
		private SensorServer sensorServer = null;
		private BobClient bobClient = null;
		private ChatServer chatServer = null;
		private Timer minuteTimer = null; 
		private bool? output = null;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			this.InitializeComponent();
			this.Suspending += OnSuspending;
		}

		/// <summary>
		/// Invoked when the application is launched normally by the end user.  Other entry points
		/// will be used such as when the application is launched to open a specific file.
		/// </summary>
		/// <param name="e">Details about the launch request and process.</param>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			Frame rootFrame = Window.Current.Content as Frame;

			// Do not repeat app initialization when the Window already has content,
			// just ensure that the window is active
			if (rootFrame == null)
			{
				// Create a Frame to act as the navigation context and navigate to the first page
				rootFrame = new Frame();

				rootFrame.NavigationFailed += OnNavigationFailed;

				if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
				{
					//TODO: Load state from previously suspended application
				}

				// Place the frame in the current Window
				Window.Current.Content = rootFrame;
			}

			if (e.PrelaunchActivated == false)
			{
				if (rootFrame.Content == null)
				{
					// When the navigation stack isn't restored navigate to the first page,
					// configuring the new page by passing required information as a navigation
					// parameter
					rootFrame.Navigate(typeof(MainPage), e.Arguments);
				}
				// Ensure the current window is active
				instance = this;
				Window.Current.Activate();
				Task.Run((Action)this.Init);
			}
		}

		private async void Init()
		{
			try
			{
				Log.Informational("Starting application.");

				Types.Initialize(
					typeof(FilesProvider).GetTypeInfo().Assembly,
					typeof(RuntimeSettings).GetTypeInfo().Assembly,
					typeof(IContentEncoder).GetTypeInfo().Assembly,
					typeof(XmppClient).GetTypeInfo().Assembly,
					typeof(Waher.Content.Markdown.MarkdownDocument).GetTypeInfo().Assembly,
					typeof(Waher.Content.Xml.XML).GetTypeInfo().Assembly,
					typeof(Waher.Script.Expression).GetTypeInfo().Assembly,
					typeof(Waher.Script.Graphs.Graph).GetTypeInfo().Assembly,
					typeof(App).GetTypeInfo().Assembly);

				Database.Register(new FilesProvider(Windows.Storage.ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Data", "Default", 8192, 1000, 8192, Encoding.UTF8, 10000));

#if GPIO
				gpio = GpioController.GetDefault();
				if (gpio != null)
				{
					if (gpio.TryOpenPin(gpioOutputPin, GpioSharingMode.Exclusive, out this.gpioPin, out GpioOpenStatus Status) &&
						Status == GpioOpenStatus.PinOpened)
					{
						if (this.gpioPin.IsDriveModeSupported(GpioPinDriveMode.Output))
						{
							this.gpioPin.SetDriveMode(GpioPinDriveMode.Output);

							this.output = await RuntimeSettings.GetAsync("Actuator.Output", false);
							this.gpioPin.Write(this.output ? GpioPinValue.High : GpioPinValue.Low);

							await MainPage.Instance.OutputSet(this.output);

							Log.Informational("Setting Control Parameter.", string.Empty, "Startup",
								new KeyValuePair<string, object>("Output", this.output));
						}
						else
							Log.Error("Output mode not supported for GPIO pin " + gpioOutputPin.ToString());
					}
					else
						Log.Error("Unable to get access to GPIO pin " + gpioOutputPin.ToString());
				}
#else
				DeviceInformationCollection Devices = await UsbSerial.listAvailableDevicesAsync();
				foreach (DeviceInformation DeviceInfo in Devices)
				{
					if (DeviceInfo.IsEnabled && DeviceInfo.Name.StartsWith("Arduino"))
					{
						Log.Informational("Connecting to " + DeviceInfo.Name);

						this.arduinoUsb = new UsbSerial(DeviceInfo);
						this.arduinoUsb.ConnectionEstablished += () =>
							Log.Informational("USB connection established.");

						this.arduino = new RemoteDevice(this.arduinoUsb);
						this.arduino.DeviceReady += async () =>
						{
							try
							{
								Log.Informational("Device ready.");

								this.arduino.pinMode(13, PinMode.OUTPUT);    // Onboard LED.
								this.arduino.digitalWrite(13, PinState.HIGH);

								this.arduino.pinMode(0, PinMode.INPUT);      // PIR sensor (motion detection).

								this.arduino.pinMode(1, PinMode.OUTPUT);     // Relay.

								this.output = await RuntimeSettings.GetAsync("Actuator.Output", false);
								this.arduino.digitalWrite(1, this.output.Value ? PinState.HIGH : PinState.LOW);

								await MainPage.Instance.OutputSet(this.output.Value);

								Log.Informational("Setting Control Parameter.", string.Empty, "Startup",
									new KeyValuePair<string, object>("Output", this.output));

								this.arduino.pinMode("A0", PinMode.ANALOG); // Light sensor.
							}
							catch (Exception ex)
							{
								Log.Critical(ex);
							}
						};

						this.arduinoUsb.ConnectionFailed += message =>
						{
							Log.Error("USB connection failed: " + message);
						};

						this.arduinoUsb.ConnectionLost += message =>
						{
							Log.Error("USB connection lost: " + message);
						};

						this.arduinoUsb.begin(57600, SerialConfig.SERIAL_8N1);
						break;
					}
				}
#endif
				this.deviceId = await RuntimeSettings.GetAsync("DeviceId", string.Empty);
				if (string.IsNullOrEmpty(this.deviceId))
				{
					this.deviceId = Guid.NewGuid().ToString().Replace("-", string.Empty);
					await RuntimeSettings.SetAsync("DeviceId", this.deviceId);
				}

				Log.Informational("Device ID: " + this.deviceId);

				string Host = await RuntimeSettings.GetAsync("XmppHost", "waher.se");
				int Port = (int)await RuntimeSettings.GetAsync("XmppPort", 5222);
				string UserName = await RuntimeSettings.GetAsync("XmppUserName", string.Empty);
				string PasswordHash = await RuntimeSettings.GetAsync("XmppPasswordHash", string.Empty);
				string PasswordHashMethod = await RuntimeSettings.GetAsync("XmppPasswordHashMethod", string.Empty);

				if (string.IsNullOrEmpty(Host) ||
					Port <= 0 || Port > ushort.MaxValue ||
					string.IsNullOrEmpty(UserName) ||
					string.IsNullOrEmpty(PasswordHash) ||
					string.IsNullOrEmpty(PasswordHashMethod))
				{
					await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
						async () => await this.ShowConnectionDialog(Host, Port, UserName));
				}
				else
				{
					this.xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, PasswordHashMethod, "en",
						typeof(App).GetTypeInfo().Assembly)     // Add "new LogSniffer()" to the end, to output communication to the log.
					{
						AllowCramMD5 = false,
						AllowDigestMD5 = false,
						AllowPlain = false,
						AllowScramSHA1 = true
					};
					this.xmppClient.OnStateChanged += this.StateChanged;
					this.xmppClient.OnConnectionError += this.ConnectionError;
					this.AttachFeatures();

					Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
					this.xmppClient.Connect();
				}

				this.minuteTimer = new Timer((State) =>
				{
					if (this.xmppClient != null &&
						(this.xmppClient.State == XmppState.Error || this.xmppClient.State == XmppState.Offline))
					{
						this.xmppClient.Reconnect();
					}

				}, null, 60000, 60000);
			}
			catch (Exception ex)
			{
				Log.Emergency(ex);

				MessageDialog Dialog = new MessageDialog(ex.Message, "Error");
				await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
					async () => await Dialog.ShowAsync());
			}
		}

		private async Task ShowConnectionDialog(string Host, int Port, string UserName)
		{
			try
			{
				AccountDialog Dialog = new AccountDialog(Host, Port, UserName);

				switch (await Dialog.ShowAsync())
				{
					case ContentDialogResult.Primary:
						if (this.xmppClient != null)
						{
							this.xmppClient.Dispose();
							this.xmppClient = null;
						}

						this.xmppClient = new XmppClient(Dialog.Host, Dialog.Port, Dialog.UserName, Dialog.Password, "en", typeof(App).GetTypeInfo().Assembly)
						{
							AllowCramMD5 = false,
							AllowDigestMD5 = false,
							AllowPlain = false
						};

						this.xmppClient.AllowRegistration();                // Allows registration on servers that do not require signatures.
																			// this.xmppClient.AllowRegistration(Key, Secret);	// Allows registration on servers requiring a signature of the registration request.

						this.xmppClient.OnStateChanged += this.TestConnectionStateChanged;
						this.xmppClient.OnConnectionError += this.ConnectionError;

						Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
						this.xmppClient.Connect();
						break;

					case ContentDialogResult.Secondary:
						break;
				}
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

		private void StateChanged(object Sender, XmppState State)
		{
			Log.Informational("Changing state: " + State.ToString());

			if (State == XmppState.Connected)
				Log.Informational("Connected as " + this.xmppClient.FullJID);
		}

		private void ConnectionError(object Sender, Exception ex)
		{
			Log.Error(ex.Message);
		}

		private void AttachFeatures()
		{
			this.controlServer = new ControlServer(this.xmppClient,
				new BooleanControlParameter("Output", "Actuator", "Output:", "Digital output.",
					(Node) => this.output,
					async (Node, Value) => await this.SetOutput(Value, "XMPP")));

			this.sensorServer = new SensorServer(this.xmppClient, true);
			this.sensorServer.OnExecuteReadoutRequest += (sender, e) =>
			{
				try
				{
					Log.Informational("Performing readout.", this.xmppClient.BareJID, e.Actor);

					List<Field> Fields = new List<Field>();
					DateTime Now = DateTime.Now;

					if (e.IsIncluded(FieldType.Identity))
						Fields.Add(new StringField(ThingReference.Empty, Now, "Device ID", this.deviceId, FieldType.Identity, FieldQoS.AutomaticReadout));

					if (this.output.HasValue)
					{
						Fields.Add(new BooleanField(ThingReference.Empty, Now, "Output", this.output.Value,
							FieldType.Momentary, FieldQoS.AutomaticReadout, true));
					}

					e.ReportFields(true, Fields);
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
			};

			this.xmppClient.OnError += (Sender, ex) => Log.Error(ex);
			this.xmppClient.OnPasswordChanged += (Sender, e) => Log.Informational("Password changed.", this.xmppClient.BareJID);

			this.xmppClient.OnPresenceSubscribe += (Sender, e) =>
			{
				Log.Informational("Accepting friendship request.", this.xmppClient.BareJID, e.From);
				e.Accept();
			};

			this.xmppClient.OnPresenceUnsubscribe += (Sender, e) =>
			{
				Log.Informational("Friendship removed.", this.xmppClient.BareJID, e.From);
				e.Accept();
			};

			this.xmppClient.OnPresenceSubscribed += (Sender, e) => Log.Informational("Friendship request accepted.", this.xmppClient.BareJID, e.From);
			this.xmppClient.OnPresenceUnsubscribed += (Sender, e) => Log.Informational("Friendship removal accepted.", this.xmppClient.BareJID, e.From);

			this.bobClient = new BobClient(this.xmppClient, Path.Combine(Path.GetTempPath(), "BitsOfBinary"));
			this.chatServer = new ChatServer(this.xmppClient, this.bobClient, this.sensorServer, this.controlServer);
		}

		private async void TestConnectionStateChanged(object Sender, XmppState State)
		{
			try
			{
				this.StateChanged(Sender, State);

				switch (State)
				{
					case XmppState.Connected:
						await RuntimeSettings.SetAsync("XmppHost", this.xmppClient.Host);
						await RuntimeSettings.SetAsync("XmppPort", this.xmppClient.Port);
						await RuntimeSettings.SetAsync("XmppUserName", this.xmppClient.UserName);
						await RuntimeSettings.SetAsync("XmppPasswordHash", this.xmppClient.PasswordHash);
						await RuntimeSettings.SetAsync("XmppPasswordHashMethod", this.xmppClient.PasswordHashMethod);

						this.xmppClient.OnStateChanged -= this.TestConnectionStateChanged;
						this.xmppClient.OnStateChanged += this.StateChanged;
						this.AttachFeatures();
						break;

					case XmppState.Error:
					case XmppState.Offline:
						await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
							async () => await this.ShowConnectionDialog(this.xmppClient.Host, this.xmppClient.Port, this.xmppClient.UserName));
						break;
				}
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

		internal static App Instance => instance;

		internal async Task SetOutput(bool On, string Actor)
		{
#if GPIO
			if (this.gpioPin != null)
			{
				this.gpioPin.Write(On ? GpioPinValue.High : GpioPinValue.Low);
#else
			if (this.arduino != null)
			{
				this.arduino.digitalWrite(1, On ? PinState.HIGH : PinState.LOW);
#endif
				await RuntimeSettings.SetAsync("Actuator.Output", On);
				this.output = On;

				this.sensorServer?.NewMomentaryValues(new BooleanField(ThingReference.Empty, DateTime.Now, "Output", On,
					FieldType.Momentary, FieldQoS.AutomaticReadout));

				Log.Informational("Setting Control Parameter.", string.Empty, Actor ?? "Windows user",
					new KeyValuePair<string, object>("Output", On));

				if (Actor != null)
					await MainPage.Instance.OutputSet(On);
			}
		}

		/// <summary>
		/// Invoked when Navigation to a certain page fails
		/// </summary>
		/// <param name="sender">The Frame which failed navigation</param>
		/// <param name="e">Details about the navigation failure</param>
		void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
		{
			throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
		}

		/// <summary>
		/// Invoked when application execution is being suspended.  Application state is saved
		/// without knowing whether the application will be terminated or resumed with the contents
		/// of memory still intact.
		/// </summary>
		/// <param name="sender">The source of the suspend request.</param>
		/// <param name="e">Details about the suspend request.</param>
		private void OnSuspending(object sender, SuspendingEventArgs e)
		{
			var deferral = e.SuspendingOperation.GetDeferral();

			if (instance == this)
				instance = null;

			if (this.chatServer != null)
			{
				this.chatServer.Dispose();
				this.chatServer = null;
			}

			if (this.bobClient != null)
			{
				this.bobClient.Dispose();
				this.bobClient = null;
			}

			if (this.sensorServer != null)
			{
				this.sensorServer.Dispose();
				this.sensorServer = null;
			}

			if (this.xmppClient != null)
			{
				this.xmppClient.Dispose();
				this.xmppClient = null;
			}

			if (this.minuteTimer != null)
			{
				this.minuteTimer.Dispose();
				this.minuteTimer = null;
			}

#if GPIO
			if (this.gpioPin != null)
			{
				this.gpioPin.Dispose();
				this.gpioPin = null;
			}
#else
			if (this.arduino != null)
			{
				this.arduino.digitalWrite(13, PinState.LOW);
				this.arduino.pinMode(13, PinMode.INPUT);     // Onboard LED.
				this.arduino.pinMode(1, PinMode.INPUT);      // Relay.

				this.arduino.Dispose();
				this.arduino = null;
			}

			if (this.arduinoUsb != null)
			{
				this.arduinoUsb.end();
				this.arduinoUsb.Dispose();
				this.arduinoUsb = null;
			}
#endif
			Log.Terminate();

			deferral.Complete();
		}
	}
}