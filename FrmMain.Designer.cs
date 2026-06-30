namespace NanotecController
{
    partial class FrmMain
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            titleLabel = new Label();
            statusGroup = new GroupBox();
            ledPanel = new Panel();
            statusLabel = new Label();
            connectButton = new Button();
            disconnectButton = new Button();
            readParamsButton = new Button();
            calibButton = new Button();
            homeAllButton = new Button();
            driveGroup = new GroupBox();
            enableButton = new Button();
            disableButton = new Button();
            inputCaption = new Label();
            rbOff = new RadioButton();
            rbUsb = new RadioButton();
            rbScreen = new RadioButton();
            joystickStatusLabel = new Label();
            onscreenGroup = new GroupBox();
            joystickPad = new JoystickPad();
            onscreenHint = new Label();
            axesGroup = new GroupBox();
            axesPanel = new Panel();
            statusTimer = new System.Windows.Forms.Timer(components);
            joystickTimer = new System.Windows.Forms.Timer(components);
            logLabel = new Label();
            logBox = new TextBox();
            statusGroup.SuspendLayout();
            driveGroup.SuspendLayout();
            onscreenGroup.SuspendLayout();
            axesGroup.SuspendLayout();
            SuspendLayout();
            // 
            // titleLabel
            // 
            titleLabel.AutoSize = true;
            titleLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            titleLabel.Location = new Point(18, 18);
            titleLabel.Name = "titleLabel";
            titleLabel.Size = new Size(412, 28);
            titleLabel.TabIndex = 0;
            titleLabel.Text = "Wafer Inspection Station - Motion Control";
            // 
            // statusGroup
            // 
            statusGroup.Controls.Add(ledPanel);
            statusGroup.Controls.Add(statusLabel);
            statusGroup.Location = new Point(18, 58);
            statusGroup.Name = "statusGroup";
            statusGroup.Size = new Size(662, 78);
            statusGroup.TabIndex = 1;
            statusGroup.TabStop = false;
            statusGroup.Text = "Link Status";
            // 
            // ledPanel
            // 
            ledPanel.BackColor = Color.Firebrick;
            ledPanel.BorderStyle = BorderStyle.FixedSingle;
            ledPanel.Location = new Point(16, 28);
            ledPanel.Name = "ledPanel";
            ledPanel.Size = new Size(29, 34);
            ledPanel.TabIndex = 0;
            // 
            // statusLabel
            // 
            statusLabel.AutoSize = true;
            statusLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            statusLabel.Location = new Point(60, 32);
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(132, 25);
            statusLabel.TabIndex = 1;
            statusLabel.Text = "Disconnected";
            // 
            // connectButton
            // 
            connectButton.Location = new Point(18, 144);
            connectButton.Name = "connectButton";
            connectButton.Size = new Size(150, 46);
            connectButton.TabIndex = 2;
            connectButton.Text = "Connect";
            connectButton.UseVisualStyleBackColor = true;
            connectButton.Click += connectButton_Click;
            // 
            // disconnectButton
            // 
            disconnectButton.Enabled = false;
            disconnectButton.Location = new Point(176, 144);
            disconnectButton.Name = "disconnectButton";
            disconnectButton.Size = new Size(150, 46);
            disconnectButton.TabIndex = 3;
            disconnectButton.Text = "Disconnect";
            disconnectButton.UseVisualStyleBackColor = true;
            disconnectButton.Click += disconnectButton_Click;
            // 
            // readParamsButton
            // 
            readParamsButton.Enabled = false;
            readParamsButton.Location = new Point(334, 144);
            readParamsButton.Name = "readParamsButton";
            readParamsButton.Size = new Size(175, 46);
            readParamsButton.TabIndex = 4;
            readParamsButton.Text = "Parameters...";
            readParamsButton.UseVisualStyleBackColor = true;
            readParamsButton.Click += readParamsButton_Click;
            // 
            // calibButton
            // 
            calibButton.Enabled = false;
            calibButton.Location = new Point(517, 144);
            calibButton.Name = "calibButton";
            calibButton.Size = new Size(150, 46);
            calibButton.TabIndex = 5;
            calibButton.Text = "Calibration...";
            calibButton.UseVisualStyleBackColor = true;
            calibButton.Click += calibButton_Click;
            // 
            // homeAllButton
            // 
            homeAllButton.Enabled = false;
            homeAllButton.Location = new Point(675, 144);
            homeAllButton.Name = "homeAllButton";
            homeAllButton.Size = new Size(150, 46);
            homeAllButton.TabIndex = 6;
            homeAllButton.Text = "Home All (Z, then X+Y)";
            homeAllButton.UseVisualStyleBackColor = true;
            homeAllButton.Click += homeAllButton_Click;
            // 
            // driveGroup
            // 
            driveGroup.Controls.Add(enableButton);
            driveGroup.Controls.Add(disableButton);
            driveGroup.Controls.Add(inputCaption);
            driveGroup.Controls.Add(rbOff);
            driveGroup.Controls.Add(rbUsb);
            driveGroup.Controls.Add(rbScreen);
            driveGroup.Controls.Add(joystickStatusLabel);
            driveGroup.Location = new Point(18, 200);
            driveGroup.Name = "driveGroup";
            driveGroup.Size = new Size(662, 100);
            driveGroup.TabIndex = 5;
            driveGroup.TabStop = false;
            driveGroup.Text = "Drive Control";
            // 
            // enableButton
            // 
            enableButton.Enabled = false;
            enableButton.Location = new Point(16, 28);
            enableButton.Name = "enableButton";
            enableButton.Size = new Size(150, 40);
            enableButton.TabIndex = 0;
            enableButton.Text = "Enable All";
            enableButton.UseVisualStyleBackColor = true;
            enableButton.Click += enableButton_Click;
            // 
            // disableButton
            // 
            disableButton.Enabled = false;
            disableButton.Location = new Point(174, 28);
            disableButton.Name = "disableButton";
            disableButton.Size = new Size(150, 40);
            disableButton.TabIndex = 1;
            disableButton.Text = "Disable All";
            disableButton.UseVisualStyleBackColor = true;
            disableButton.Click += disableButton_Click;
            // 
            // inputCaption
            // 
            inputCaption.AutoSize = true;
            inputCaption.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            inputCaption.Location = new Point(16, 72);
            inputCaption.Name = "inputCaption";
            inputCaption.Size = new Size(51, 20);
            inputCaption.TabIndex = 2;
            inputCaption.Text = "Input:";
            // 
            // rbOff
            // 
            rbOff.AutoSize = true;
            rbOff.Checked = true;
            rbOff.Enabled = false;
            rbOff.Location = new Point(72, 70);
            rbOff.Name = "rbOff";
            rbOff.Size = new Size(51, 24);
            rbOff.TabIndex = 3;
            rbOff.TabStop = true;
            rbOff.Text = "Off";
            rbOff.UseVisualStyleBackColor = true;
            rbOff.CheckedChanged += inputSourceChanged;
            // 
            // rbUsb
            // 
            rbUsb.AutoSize = true;
            rbUsb.Enabled = false;
            rbUsb.Location = new Point(130, 70);
            rbUsb.Name = "rbUsb";
            rbUsb.Size = new Size(110, 24);
            rbUsb.TabIndex = 4;
            rbUsb.Text = "USB joystick";
            rbUsb.UseVisualStyleBackColor = true;
            rbUsb.CheckedChanged += inputSourceChanged;
            // 
            // rbScreen
            // 
            rbScreen.AutoSize = true;
            rbScreen.Enabled = false;
            rbScreen.Location = new Point(258, 70);
            rbScreen.Name = "rbScreen";
            rbScreen.Size = new Size(97, 24);
            rbScreen.TabIndex = 5;
            rbScreen.Text = "On-screen";
            rbScreen.UseVisualStyleBackColor = true;
            rbScreen.CheckedChanged += inputSourceChanged;
            // 
            // joystickStatusLabel
            // 
            joystickStatusLabel.AutoSize = true;
            joystickStatusLabel.Location = new Point(370, 72);
            joystickStatusLabel.Name = "joystickStatusLabel";
            joystickStatusLabel.Size = new Size(69, 20);
            joystickStatusLabel.TabIndex = 6;
            joystickStatusLabel.Text = "Input: off";
            // 
            // onscreenGroup
            // 
            onscreenGroup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            onscreenGroup.Controls.Add(joystickPad);
            onscreenGroup.Controls.Add(onscreenHint);
            onscreenGroup.Location = new Point(694, 200);
            onscreenGroup.Name = "onscreenGroup";
            onscreenGroup.Size = new Size(168, 250);
            onscreenGroup.TabIndex = 7;
            onscreenGroup.TabStop = false;
            onscreenGroup.Text = "On-screen Joystick (XY)";
            // 
            // joystickPad
            // 
            joystickPad.Enabled = false;
            joystickPad.Location = new Point(21, 50);
            joystickPad.Name = "joystickPad";
            joystickPad.Size = new Size(128, 125);
            joystickPad.TabIndex = 0;
            // 
            // onscreenHint
            // 
            onscreenHint.Location = new Point(12, 178);
            onscreenHint.Name = "onscreenHint";
            onscreenHint.Size = new Size(146, 62);
            onscreenHint.TabIndex = 1;
            onscreenHint.Text = "Drag the puck to jog X/Y. Release = stop. Direction = angle, speed = distance (rim = X/Y sliders).";
            // 
            // axesGroup
            // 
            axesGroup.Controls.Add(axesPanel);
            axesGroup.Location = new Point(18, 388);
            axesGroup.Name = "axesGroup";
            axesGroup.Size = new Size(662, 360);
            axesGroup.TabIndex = 8;
            axesGroup.TabStop = false;
            axesGroup.Text = "Axis Jog - slider sets speed, hold an arrow for direction";
            // 
            // axesPanel
            // 
            axesPanel.Location = new Point(14, 28);
            axesPanel.Name = "axesPanel";
            axesPanel.Size = new Size(636, 316);
            axesPanel.TabIndex = 0;
            // 
            // statusTimer
            // 
            statusTimer.Interval = 200;
            statusTimer.Tick += statusTimer_Tick;
            // 
            // joystickTimer
            // 
            joystickTimer.Interval = 50;
            joystickTimer.Tick += joystickTimer_Tick;
            // 
            // logLabel
            // 
            logLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            logLabel.AutoSize = true;
            logLabel.Location = new Point(18, 758);
            logLabel.Name = "logLabel";
            logLabel.Size = new Size(37, 20);
            logLabel.TabIndex = 9;
            logLabel.Text = "Log:";
            // 
            // logBox
            // 
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            logBox.BackColor = Color.White;
            logBox.Font = new Font("Consolas", 9F);
            logBox.Location = new Point(18, 782);
            logBox.Multiline = true;
            logBox.Name = "logBox";
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Size = new Size(844, 178);
            logBox.TabIndex = 10;
            // 
            // FrmMain
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(880, 980);
            Controls.Add(titleLabel);
            Controls.Add(statusGroup);
            Controls.Add(connectButton);
            Controls.Add(disconnectButton);
            Controls.Add(readParamsButton);
            Controls.Add(calibButton);
            Controls.Add(homeAllButton);
            Controls.Add(driveGroup);
            Controls.Add(onscreenGroup);
            Controls.Add(axesGroup);
            Controls.Add(logLabel);
            Controls.Add(logBox);
            Font = new Font("Segoe UI", 9F);
            MinimumSize = new Size(760, 940);
            Name = "FrmMain";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Nanotec Controller - Motion";
            Activated += FrmMain_Activated;
            Deactivate += FrmMain_Deactivate;
            FormClosing += FrmMain_FormClosing;
            statusGroup.ResumeLayout(false);
            statusGroup.PerformLayout();
            driveGroup.ResumeLayout(false);
            driveGroup.PerformLayout();
            onscreenGroup.ResumeLayout(false);
            axesGroup.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label titleLabel;
        private System.Windows.Forms.GroupBox statusGroup;
        private System.Windows.Forms.Panel ledPanel;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.Button connectButton;
        private System.Windows.Forms.Button disconnectButton;
        private System.Windows.Forms.Button readParamsButton;
        private System.Windows.Forms.Button calibButton;
        private System.Windows.Forms.Button homeAllButton;
        private System.Windows.Forms.GroupBox driveGroup;
        private System.Windows.Forms.Button enableButton;
        private System.Windows.Forms.Button disableButton;
        private System.Windows.Forms.Label inputCaption;
        private System.Windows.Forms.RadioButton rbOff;
        private System.Windows.Forms.RadioButton rbUsb;
        private System.Windows.Forms.RadioButton rbScreen;
        private System.Windows.Forms.Label joystickStatusLabel;
        private System.Windows.Forms.GroupBox onscreenGroup;
        private JoystickPad joystickPad;
        private System.Windows.Forms.Label onscreenHint;
        private System.Windows.Forms.GroupBox axesGroup;
        private System.Windows.Forms.Panel axesPanel;
        private System.Windows.Forms.Timer statusTimer;
        private System.Windows.Forms.Timer joystickTimer;
        private System.Windows.Forms.Label logLabel;
        private System.Windows.Forms.TextBox logBox;
    }
}
