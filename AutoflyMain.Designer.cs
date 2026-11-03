namespace AutoFly
{
    partial class AutoflyMain
    {
        /// <summary>
        ///  Erforderliche Designer-Variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Alle verwendeten Ressourcen bereinigen.
        /// </summary>
        /// <param name="disposing">true, wenn verwaltete Ressourcen freigegeben werden sollen; andernfalls false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Erforderliche Methode für die Designer-Unterstützung - den Inhalt dieser Methode nicht mit dem Code-Editor ändern.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Resources_DE));
            toolStripTop = new ToolStrip();
            tslPlaneCenter = new ToolStripLabel();
            TsbPlaneCenter = new ToolStripButton();
            toolStripSeparator3 = new ToolStripSeparator();
            tslFlightPlan = new ToolStripLabel();
            TsbFollow = new ToolStripButton();
            tsbClear = new ToolStripButton();
            tsbReset = new ToolStripButton();
            tsbSave = new ToolStripButton();
            TsbLoad = new ToolStripButton();
            toolStripSeparator4 = new ToolStripSeparator();
            tsbGerman = new ToolStripButton();
            tsbEnglish = new ToolStripButton();
            tsbInfo = new ToolStripButton();
            toolStripBottom = new ToolStrip();
            tsbAP = new ToolStripButton();
            tslAltitude = new ToolStripLabel();
            tstbAltitude = new ToolStripTextBox();
            GetCurrALT = new ToolStripButton();
            tsbALT = new ToolStripButton();
            TsbATT = new ToolStripButton();
            tslHeading = new ToolStripLabel();
            tstbHeading = new ToolStripTextBox();
            GetCurrHDG = new ToolStripButton();
            tsbHDG = new ToolStripButton();
            TsbLVL = new ToolStripButton();
            toolStripSeparator1 = new ToolStripSeparator();
            tsbAutoThrottle = new ToolStripButton();
            toolStripSeparator2 = new ToolStripSeparator();
            TsbAutoElevation = new ToolStripButton();
            tsbTakeOff = new ToolStripButton();
            tsbAutoland = new ToolStripButton();
            panel1 = new Panel();
            autoStartDialog = new AutoStartDialog();
            webView2 = new Microsoft.Web.WebView2.WinForms.WebView2();
            statusStripBottom = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            toolStripTop.SuspendLayout();
            toolStripBottom.SuspendLayout();
            panel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)webView2).BeginInit();
            statusStripBottom.SuspendLayout();
            SuspendLayout();
            // 
            // toolStripTop
            // 
            toolStripTop.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            toolStripTop.AutoSize = false;
            toolStripTop.CanOverflow = false;
            toolStripTop.Dock = DockStyle.None;
            toolStripTop.Items.AddRange(new ToolStripItem[] { tslPlaneCenter, TsbPlaneCenter, toolStripSeparator3, tslFlightPlan, TsbFollow, tsbClear, tsbReset, tsbSave, TsbLoad, toolStripSeparator4, tsbGerman, tsbEnglish, tsbInfo });
            toolStripTop.Location = new Point(0, 0);
            toolStripTop.Name = "toolStripTop";
            toolStripTop.Size = new Size(1083, 39);
            toolStripTop.TabIndex = 0;
            toolStripTop.Text = Resources_DE.GetString("MainForm_TopToolbar");
            // 
            // tslPlaneCenter
            // 
            tslPlaneCenter.Name = "tslPlaneCenter";
            tslPlaneCenter.Size = new Size(0, 36);
            tslPlaneCenter.Text = Resources_DE.GetString("MainForm_AircraftPosition");
            // 
            // TsbPlaneCenter
            // 
            TsbPlaneCenter.BackColor = Color.LightGreen;
            TsbPlaneCenter.DisplayStyle = ToolStripItemDisplayStyle.Text;
            TsbPlaneCenter.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            TsbPlaneCenter.ImageTransparentColor = Color.Magenta;
            TsbPlaneCenter.Margin = new Padding(5, 1, 0, 2);
            TsbPlaneCenter.Name = "TsbPlaneCenter";
            TsbPlaneCenter.Size = new Size(23, 36);
            TsbPlaneCenter.Text = Resources_DE.GetString("MainForm_CenterMap");
            TsbPlaneCenter.Click += TsbPlaneCenter_Click;
            // 
            // toolStripSeparator3
            // 
            toolStripSeparator3.AutoSize = false;
            toolStripSeparator3.Name = "toolStripSeparator3";
            toolStripSeparator3.Size = new Size(20, 39);
            // 
            // tslFlightPlan
            // 
            tslFlightPlan.Margin = new Padding(5, 1, 0, 2);
            tslFlightPlan.Name = "tslFlightPlan";
            tslFlightPlan.Size = new Size(0, 36);
            tslFlightPlan.Text = Resources_DE.GetString("MainForm_FlightPlan");
            // 
            // TsbFollow
            // 
            TsbFollow.AutoSize = false;
            TsbFollow.BackColor = SystemColors.ControlLight;
            TsbFollow.DisplayStyle = ToolStripItemDisplayStyle.Text;
            TsbFollow.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            TsbFollow.Image = (Image)resources.GetObject("TsbFollow.Image");
            TsbFollow.ImageTransparentColor = Color.Magenta;
            TsbFollow.Margin = new Padding(5, 1, 0, 2);
            TsbFollow.Name = "TsbFollow";
            TsbFollow.Size = new Size(55, 36);
            TsbFollow.Text = Resources_DE.GetString("MainForm_Follow");
            TsbFollow.ToolTipText = Resources_DE.GetString("MainForm_FollowToolTip");
            TsbFollow.Click += TsbFollow_Click;
            // 
            // tsbClear
            // 
            tsbClear.AutoSize = false;
            tsbClear.BackColor = SystemColors.ControlLight;
            tsbClear.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbClear.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbClear.Image = (Image)resources.GetObject("tsbClear.Image");
            tsbClear.ImageTransparentColor = Color.Magenta;
            tsbClear.Margin = new Padding(5, 1, 0, 2);
            tsbClear.Name = "tsbClear";
            tsbClear.Size = new Size(55, 36);
            tsbClear.Text = Resources_DE.GetString("MainForm_Clear");
            tsbClear.ToolTipText = Resources_DE.GetString("MainForm_ClearToolTip");
            // 
            // tsbReset
            // 
            tsbReset.AutoSize = false;
            tsbReset.BackColor = SystemColors.ControlLight;
            tsbReset.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbReset.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbReset.Image = (Image)resources.GetObject("tsbReset.Image");
            tsbReset.ImageTransparentColor = Color.Magenta;
            tsbReset.Margin = new Padding(5, 1, 0, 2);
            tsbReset.Name = "tsbReset";
            tsbReset.Size = new Size(55, 36);
            tsbReset.Text = Resources_DE.GetString("MainForm_Reset");
            tsbReset.ToolTipText = Resources_DE.GetString("MainForm_ResetToolTip");
            // 
            // tsbSave
            // 
            tsbSave.AutoSize = false;
            tsbSave.BackColor = SystemColors.ControlLight;
            tsbSave.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbSave.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbSave.Image = (Image)resources.GetObject("tsbSave.Image");
            tsbSave.ImageTransparentColor = Color.Magenta;
            tsbSave.Margin = new Padding(5, 1, 0, 2);
            tsbSave.Name = "tsbSave";
            tsbSave.Size = new Size(55, 36);
            tsbSave.Text = Resources_DE.GetString("MainForm_Save");
            tsbSave.ToolTipText = Resources_DE.GetString("MainForm_SaveToolTip");
            // 
            // TsbLoad
            // 
            TsbLoad.AutoSize = false;
            TsbLoad.BackColor = SystemColors.ControlLight;
            TsbLoad.DisplayStyle = ToolStripItemDisplayStyle.Text;
            TsbLoad.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            TsbLoad.Image = (Image)resources.GetObject("TsbLoad.Image");
            TsbLoad.ImageTransparentColor = Color.Magenta;
            TsbLoad.Margin = new Padding(5, 1, 0, 2);
            TsbLoad.Name = "TsbLoad";
            TsbLoad.Size = new Size(55, 36);
            TsbLoad.Text = Resources_DE.GetString("MainForm_Load");
            TsbLoad.ToolTipText = Resources_DE.GetString("MainForm_LoadToolTip");
            // 
            // toolStripSeparator4
            // 
            toolStripSeparator4.AutoSize = false;
            toolStripSeparator4.Name = "toolStripSeparator4";
            toolStripSeparator4.Size = new Size(20, 39);
            // 
            // tsbGerman
            // 
            tsbGerman.AutoSize = false;
            tsbGerman.BackColor = SystemColors.ControlLight;
            tsbGerman.DisplayStyle = ToolStripItemDisplayStyle.Image;
            tsbGerman.Image = (Image)resources.GetObject("tsbGerman.Image");
            tsbGerman.ImageTransparentColor = Color.Magenta;
            tsbGerman.Margin = new Padding(5, 1, 0, 2);
            tsbGerman.Name = "tsbGerman";
            tsbGerman.Size = new Size(36, 36);
            tsbGerman.Text = Resources_DE.GetString("MainForm_German");
            // 
            // tsbEnglish
            // 
            tsbEnglish.AutoSize = false;
            tsbEnglish.BackColor = SystemColors.ControlLight;
            tsbEnglish.DisplayStyle = ToolStripItemDisplayStyle.Image;
            tsbEnglish.Image = (Image)resources.GetObject("tsbEnglish.Image");
            tsbEnglish.ImageTransparentColor = Color.Magenta;
            tsbEnglish.Margin = new Padding(5, 1, 0, 2);
            tsbEnglish.Name = "tsbEnglish";
            tsbEnglish.Size = new Size(36, 36);
            tsbEnglish.Text = Resources_DE.GetString("MainForm_English");
            // 
            // tsbInfo
            // 
            tsbInfo.AutoSize = false;
            tsbInfo.BackColor = SystemColors.ControlLight;
            tsbInfo.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbInfo.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
            tsbInfo.Image = (Image)resources.GetObject("tsbInfo.Image");
            tsbInfo.ImageTransparentColor = Color.Magenta;
            tsbInfo.Margin = new Padding(5, 1, 0, 2);
            tsbInfo.Name = "tsbInfo";
            tsbInfo.Size = new Size(36, 36);
            tsbInfo.Text = Resources_DE.GetString("MainForm_Information");
            tsbInfo.ToolTipText = Resources_DE.GetString("MainForm_InformationToolTip");
            // 
            // toolStripBottom
            // 
            toolStripBottom.Dock = DockStyle.Bottom;
            toolStripBottom.Items.AddRange(new ToolStripItem[] { tsbAP, tslAltitude, tstbAltitude, GetCurrALT, tsbALT, TsbATT, tslHeading, tstbHeading, GetCurrHDG, tsbHDG, TsbLVL, toolStripSeparator1, tsbAutoThrottle, toolStripSeparator2, TsbAutoElevation, tsbTakeOff, tsbAutoland });
            toolStripBottom.Location = new Point(0, 641);
            toolStripBottom.Name = "toolStripBottom";
            toolStripBottom.Size = new Size(1083, 38);
            toolStripBottom.TabIndex = 2;
            toolStripBottom.Text = Resources_DE.GetString("MainForm_BottomToolbar");
            // 
            // tsbAP
            // 
            tsbAP.AutoSize = false;
            tsbAP.BackColor = Color.LightGray;
            tsbAP.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbAP.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbAP.Image = (Image)resources.GetObject("tsbAP.Image");
            tsbAP.ImageTransparentColor = Color.Magenta;
            tsbAP.Name = "tsbAP";
            tsbAP.Size = new Size(35, 35);
            tsbAP.Text = Resources_DE.GetString("MainForm_Autopilot");
            tsbAP.ToolTipText = Resources_DE.GetString("MainForm_AutopilotToolTip");
            tsbAP.Click += TsbAP_Click;
            // 
            // tslAltitude
            // 
            tslAltitude.Margin = new Padding(5, 1, 0, 2);
            tslAltitude.Name = "tslAltitude";
            tslAltitude.Size = new Size(0, 35);
            tslAltitude.Text = Resources_DE.GetString("MainForm_TargetAltitude");
            // 
            // tstbAltitude
            // 
            tstbAltitude.BackColor = SystemColors.ControlLight;
            tstbAltitude.Name = "tstbAltitude";
            tstbAltitude.Size = new Size(100, 38);
            tstbAltitude.Text = Resources_DE.GetString("MainForm_InitialAltitude");
            tstbAltitude.ToolTipText = Resources_DE.GetString("MainForm_AltitudeToolTip");
            tstbAltitude.KeyDown += TstbAltitude_KeyDown;
            // 
            // GetCurrALT
            // 
            GetCurrALT.AutoSize = false;
            GetCurrALT.BackColor = SystemColors.ControlLight;
            GetCurrALT.DisplayStyle = ToolStripItemDisplayStyle.Text;
            GetCurrALT.Image = (Image)resources.GetObject("GetCurrALT.Image");
            GetCurrALT.ImageTransparentColor = Color.Magenta;
            GetCurrALT.Name = "GetCurrALT";
            GetCurrALT.Size = new Size(24, 24);
            GetCurrALT.Text = Resources_DE.GetString("MainForm_UseCurrentValue");
            GetCurrALT.ToolTipText = Resources_DE.GetString("MainForm_CurrentAltitudeToolTip");
            GetCurrALT.Click += GetCurrALT_Click;
            // 
            // tsbALT
            // 
            tsbALT.AutoSize = false;
            tsbALT.BackColor = Color.LightGray;
            tsbALT.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbALT.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbALT.Image = (Image)resources.GetObject("tsbALT.Image");
            tsbALT.ImageTransparentColor = Color.Magenta;
            tsbALT.Margin = new Padding(5, 1, 0, 2);
            tsbALT.Name = "tsbALT";
            tsbALT.Size = new Size(35, 35);
            tsbALT.Text = Resources_DE.GetString("MainForm_AltitudeControl");
            tsbALT.ToolTipText = Resources_DE.GetString("MainForm_AltitudeControlToolTip");
            tsbALT.Click += TsbALT_Click;
            // 
            // TsbATT
            // 
            TsbATT.BackColor = Color.LightGray;
            TsbATT.DisplayStyle = ToolStripItemDisplayStyle.Text;
            TsbATT.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            TsbATT.Image = (Image)resources.GetObject("TsbATT.Image");
            TsbATT.ImageTransparentColor = Color.Magenta;
            TsbATT.Margin = new Padding(5, 1, 0, 2);
            TsbATT.Name = "TsbATT";
            TsbATT.Size = new Size(23, 35);
            TsbATT.Text = Resources_DE.GetString("MainForm_AttitudeControl");
            TsbATT.ToolTipText = Resources_DE.GetString("MainForm_AttitudeControlToolTip");
            TsbATT.Click += TsbATT_Click;
            // 
            // tslHeading
            // 
            tslHeading.Margin = new Padding(5, 1, 0, 2);
            tslHeading.Name = "tslHeading";
            tslHeading.Size = new Size(0, 35);
            tslHeading.Text = Resources_DE.GetString("MainForm_TargetHeading");
            // 
            // tstbHeading
            // 
            tstbHeading.BackColor = SystemColors.ControlLight;
            tstbHeading.Name = "tstbHeading";
            tstbHeading.Size = new Size(100, 38);
            tstbHeading.Text = Resources_DE.GetString("MainForm_InitialHeading");
            tstbHeading.ToolTipText = Resources_DE.GetString("MainForm_HeadingToolTip");
            tstbHeading.KeyDown += TstbHeading_KeyDown;
            // 
            // GetCurrHDG
            // 
            GetCurrHDG.AutoSize = false;
            GetCurrHDG.BackColor = SystemColors.ControlLight;
            GetCurrHDG.DisplayStyle = ToolStripItemDisplayStyle.Text;
            GetCurrHDG.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            GetCurrHDG.Image = (Image)resources.GetObject("GetCurrHDG.Image");
            GetCurrHDG.ImageTransparentColor = Color.Magenta;
            GetCurrHDG.Name = "GetCurrHDG";
            GetCurrHDG.Size = new Size(24, 24);
            GetCurrHDG.Text = Resources_DE.GetString("MainForm_UseCurrentValue");
            GetCurrHDG.ToolTipText = Resources_DE.GetString("MainForm_CurrentHeadingToolTip");
            GetCurrHDG.Click += GetCurrHDG_Click;
            // 
            // tsbHDG
            // 
            tsbHDG.AutoSize = false;
            tsbHDG.BackColor = Color.LightGray;
            tsbHDG.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbHDG.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbHDG.Image = (Image)resources.GetObject("tsbHDG.Image");
            tsbHDG.ImageTransparentColor = Color.Magenta;
            tsbHDG.Margin = new Padding(5, 1, 0, 2);
            tsbHDG.Name = "tsbHDG";
            tsbHDG.RightToLeft = RightToLeft.Yes;
            tsbHDG.Size = new Size(35, 35);
            tsbHDG.Text = Resources_DE.GetString("MainForm_HeadingControl");
            tsbHDG.ToolTipText = Resources_DE.GetString("MainForm_HeadingControlToolTip");
            tsbHDG.Click += TsbHDG_Click;
            // 
            // TsbLVL
            // 
            TsbLVL.BackColor = Color.LightGray;
            TsbLVL.DisplayStyle = ToolStripItemDisplayStyle.Text;
            TsbLVL.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            TsbLVL.Image = (Image)resources.GetObject("TsbLVL.Image");
            TsbLVL.ImageTransparentColor = Color.Magenta;
            TsbLVL.Margin = new Padding(5, 1, 0, 2);
            TsbLVL.Name = "TsbLVL";
            TsbLVL.Size = new Size(23, 35);
            TsbLVL.Text = Resources_DE.GetString("MainForm_LevelControl");
            TsbLVL.ToolTipText = Resources_DE.GetString("MainForm_LevelControlToolTip");
            TsbLVL.Click += TsbLVL_Click;
            // 
            // toolStripSeparator1
            // 
            toolStripSeparator1.AutoSize = false;
            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(20, 38);
            // 
            // tsbAutoThrottle
            // 
            tsbAutoThrottle.AccessibleDescription = Resources_DE.GetString("MainForm_AutoThrottleDescription");
            tsbAutoThrottle.AccessibleName = Resources_DE.GetString("MainForm_AutoThrottleName");
            tsbAutoThrottle.BackColor = Color.LightGray;
            tsbAutoThrottle.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbAutoThrottle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbAutoThrottle.Margin = new Padding(5, 1, 0, 2);
            tsbAutoThrottle.Name = "tsbAutoThrottle";
            tsbAutoThrottle.Size = new Size(23, 35);
            tsbAutoThrottle.Text = Resources_DE.GetString("MainForm_AutoThrottle");
            tsbAutoThrottle.ToolTipText = Resources_DE.GetString("MainForm_AutoThrottleToolTip");
            tsbAutoThrottle.Click += TsbAutoThrottle_Click;
            // 
            // toolStripSeparator2
            // 
            toolStripSeparator2.AutoSize = false;
            toolStripSeparator2.Name = "toolStripSeparator2";
            toolStripSeparator2.Size = new Size(20, 38);
            // 
            // TsbAutoElevation
            // 
            TsbAutoElevation.BackColor = Color.LightGray;
            TsbAutoElevation.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            TsbAutoElevation.ImageAlign = ContentAlignment.MiddleLeft;
            TsbAutoElevation.ImageTransparentColor = Color.Magenta;
            TsbAutoElevation.Margin = new Padding(5, 1, 0, 2);
            TsbAutoElevation.Name = "TsbAutoElevation";
            TsbAutoElevation.Size = new Size(23, 35);
            TsbAutoElevation.Text = Resources_DE.GetString("MainForm_AutoElevation");
            TsbAutoElevation.ToolTipText = Resources_DE.GetString("MainForm_AutoElevationToolTip");
            TsbAutoElevation.Click += TsbAutoElevation_Click;
            // 
            // tsbTakeOff
            // 
            tsbTakeOff.BackColor = Color.LightGray;
            tsbTakeOff.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbTakeOff.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbTakeOff.Image = (Image)resources.GetObject("tsbTakeOff.Image");
            tsbTakeOff.ImageTransparentColor = Color.Magenta;
            tsbTakeOff.Margin = new Padding(5, 1, 0, 2);
            tsbTakeOff.Name = "tsbTakeOff";
            tsbTakeOff.Size = new Size(23, 35);
            tsbTakeOff.Text = Resources_DE.GetString("MainForm_Takeoff");
            tsbTakeOff.ToolTipText = Resources_DE.GetString("MainForm_TakeoffToolTip");
            tsbTakeOff.Click += TsbTakeOff_Click;
            // 
            // tsbAutoland
            // 
            tsbAutoland.BackColor = Color.LightGray;
            tsbAutoland.DisplayStyle = ToolStripItemDisplayStyle.Text;
            tsbAutoland.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            tsbAutoland.Image = (Image)resources.GetObject("tsbAutoland.Image");
            tsbAutoland.ImageTransparentColor = Color.Magenta;
            tsbAutoland.Margin = new Padding(5, 1, 0, 2);
            tsbAutoland.Name = "tsbAutoland";
            tsbAutoland.Size = new Size(23, 35);
            tsbAutoland.Text = Resources_DE.GetString("MainForm_AutoLand");
            tsbAutoland.ToolTipText = Resources_DE.GetString("MainForm_AutoLandToolTip");
            // 
            // panel1
            // 
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            panel1.AutoSize = true;
            panel1.Controls.Add(autoStartDialog);
            panel1.Controls.Add(webView2);
            panel1.Location = new Point(0, 42);
            panel1.Name = "panel1";
            panel1.Size = new Size(1083, 596);
            panel1.TabIndex = 3;
            // 
            // autoStartDialog
            // 
            autoStartDialog.AccessibleDescription = Resources_DE.GetString("autoStartDialog.AccessibleDescription");
            autoStartDialog.AccessibleName = "Automatischer Start blockiert:";
            autoStartDialog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            autoStartDialog.BorderStyle = BorderStyle.FixedSingle;
            autoStartDialog.Location = new Point(16, 24);
            autoStartDialog.MinimumSize = new Size(280, 340);
            autoStartDialog.Name = "autoStartDialog";
            autoStartDialog.Padding = new Padding(10);
            autoStartDialog.Size = new Size(340, 548);
            autoStartDialog.TabIndex = 6;
            autoStartDialog.Visible = false;
            // 
            // webView2
            // 
            webView2.AllowExternalDrop = true;
            webView2.CreationProperties = null;
            webView2.DefaultBackgroundColor = Color.White;
            webView2.Dock = DockStyle.Fill;
            webView2.Location = new Point(0, 0);
            webView2.Name = "webView2";
            webView2.Size = new Size(1083, 596);
            webView2.TabIndex = 5;
            webView2.ZoomFactor = 1D;
            // 
            // statusStripBottom
            // 
            statusStripBottom.Items.AddRange(new ToolStripItem[] { statusLabel });
            statusStripBottom.Location = new Point(0, 679);
            statusStripBottom.Name = "statusStripBottom";
            statusStripBottom.Size = new Size(1083, 22);
            statusStripBottom.TabIndex = 4;
            // 
            // statusLabel
            // 
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(0, 17);
            statusLabel.Text = Resources_DE.GetString("MainForm_Ready");
            // 
            // AutoflyMain
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1083, 701);
            Controls.Add(panel1);
            Controls.Add(toolStripBottom);
            Controls.Add(statusStripBottom);
            Controls.Add(toolStripTop);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "AutoflyMain";
            Text = Resources_DE.GetString("MainForm_Title");
            toolStripTop.ResumeLayout(false);
            toolStripTop.PerformLayout();
            toolStripBottom.ResumeLayout(false);
            toolStripBottom.PerformLayout();
            panel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)webView2).EndInit();
            statusStripBottom.ResumeLayout(false);
            statusStripBottom.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        private System.Windows.Forms.ToolStrip toolStripTop;
        private System.Windows.Forms.ToolStrip toolStripBottom;
        private System.Windows.Forms.StatusStrip statusStripBottom;
        private System.Windows.Forms.ToolStripStatusLabel statusLabel;
        private Microsoft.Web.WebView2.WinForms.WebView2 webView2;
        private AutoStartDialog autoStartDialog;
        private System.Windows.Forms.ToolStripButton TsbPlaneCenter;

        #endregion

            private Panel panel1;
        private ToolStripButton tsbAP;
        private ToolStripLabel tslPlaneCenter;
        private ToolStripLabel tslFlightPlan;
        private ToolStripButton tsbClear;
        private ToolStripButton tsbReset;
        private ToolStripButton tsbSave;
        private ToolStripLabel tslAltitude;
        private ToolStripTextBox tstbAltitude;
        private ToolStripButton tsbALT;
        private ToolStripLabel tslHeading;
        private ToolStripTextBox tstbHeading;
        private ToolStripButton tsbHDG;
        private ToolStripButton tsbTakeOff;
        private ToolStripButton tsbAutoland;
        private ToolStripButton TsbAutoElevation;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripButton GetCurrHDG;
        private ToolStripButton GetCurrALT;
        private ToolStripButton TsbATT;
        private ToolStripButton TsbLVL;
        private ToolStripButton tsbAutoThrottle;
        private ToolStripSeparator toolStripSeparator2;
        private ToolStripSeparator toolStripSeparator3;
        private ToolStripButton TsbFollow;
        private ToolStripButton TsbLoad;
        private ToolStripSeparator toolStripSeparator4;
        private ToolStripButton tsbGerman;
        private ToolStripButton tsbEnglish;
        private ToolStripButton tsbInfo;
    }
}
