namespace RealtimePlayGround
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Button btnRecord;
        private System.Windows.Forms.Button btnPlay;
        private System.Windows.Forms.Button btnCall;
        private System.Windows.Forms.Button btnSend;
        private System.Windows.Forms.Label statusLabel;
        private System.Windows.Forms.SplitContainer splitContainer1;
        private System.Windows.Forms.RichTextBox richTextBox1;
        private System.Windows.Forms.RichTextBox richTextBoxEvents;
        private System.Windows.Forms.RichTextBox richTextBox2;
        private System.Windows.Forms.RichTextBox richTextBoxLogs;
        private System.Windows.Forms.Label lblVoice;
        private System.Windows.Forms.ComboBox cmbVoice;
        private System.Windows.Forms.Label lblSpeed;
        private System.Windows.Forms.TrackBar trackSpeed;
        private System.Windows.Forms.Label lblLogLevel;
        private System.Windows.Forms.ComboBox cmbLogLevel;
        private System.Windows.Forms.Label lblProvider;
        private System.Windows.Forms.ComboBox cmbProvider;
        private System.Windows.Forms.CheckBox chkVadEnabled;
        private System.Windows.Forms.CheckBox chkAllowInterruption;
        private System.Windows.Forms.SplitContainer splitContainer2;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.btnRecord = new System.Windows.Forms.Button();
            this.btnPlay = new System.Windows.Forms.Button();
            this.btnCall = new System.Windows.Forms.Button();
            this.btnSend = new System.Windows.Forms.Button();
            this.statusLabel = new System.Windows.Forms.Label();
            this.splitContainer1 = new System.Windows.Forms.SplitContainer();
            this.splitContainer2 = new System.Windows.Forms.SplitContainer();
            this.richTextBox1 = new System.Windows.Forms.RichTextBox();
            this.richTextBoxEvents = new System.Windows.Forms.RichTextBox();
            this.richTextBoxLogs = new System.Windows.Forms.RichTextBox();
            this.richTextBox2 = new System.Windows.Forms.RichTextBox();
            this.lblVoice = new System.Windows.Forms.Label();
            this.cmbVoice = new System.Windows.Forms.ComboBox();
            this.lblSpeed = new System.Windows.Forms.Label();
            this.trackSpeed = new System.Windows.Forms.TrackBar();
            this.lblLogLevel = new System.Windows.Forms.Label();
            this.cmbLogLevel = new System.Windows.Forms.ComboBox();
            this.lblProvider = new System.Windows.Forms.Label();
            this.cmbProvider = new System.Windows.Forms.ComboBox();
            this.chkVadEnabled = new System.Windows.Forms.CheckBox();
            this.chkAllowInterruption = new System.Windows.Forms.CheckBox();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.trackSpeed)).BeginInit();
            this.splitContainer1.Panel1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            this.splitContainer2.Panel1.SuspendLayout();
            this.splitContainer2.Panel2.SuspendLayout();
            this.splitContainer2.SuspendLayout();
            this.SuspendLayout();
            //
            // btnRecord
            //
            this.btnRecord.Enabled = false;
            this.btnRecord.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnRecord.ImageAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.btnRecord.Location = new System.Drawing.Point(12, 12);
            this.btnRecord.Name = "btnRecord";
            this.btnRecord.Size = new System.Drawing.Size(50, 50);
            this.btnRecord.TabIndex = 0;
            this.btnRecord.Text = "";
            this.btnRecord.UseVisualStyleBackColor = true;
            this.btnRecord.Click += new System.EventHandler(this.btnRecord_Click);
            //
            // btnPlay
            //
            this.btnPlay.Enabled = false;
            this.btnPlay.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnPlay.Location = new System.Drawing.Point(12, 68);
            this.btnPlay.Name = "btnPlay";
            this.btnPlay.Size = new System.Drawing.Size(50, 50);
            this.btnPlay.TabIndex = 1;
            this.btnPlay.Text = "";
            this.btnPlay.UseVisualStyleBackColor = true;
            this.btnPlay.Visible = false;
            this.btnPlay.Click += new System.EventHandler(this.btnPlay_Click);
            //
            // btnCall
            //
            this.btnCall.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.btnCall.Location = new System.Drawing.Point(12, 68);
            this.btnCall.Name = "btnCall";
            this.btnCall.Size = new System.Drawing.Size(50, 50);
            this.btnCall.TabIndex = 5;
            this.btnCall.Text = "";
            this.btnCall.UseVisualStyleBackColor = true;
            this.btnCall.Click += new System.EventHandler(this.btnCall_Click);
            //
            // lblVoice
            //
            this.lblVoice.AutoSize = true;
            this.lblVoice.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblVoice.Location = new System.Drawing.Point(12, 130);
            this.lblVoice.Name = "lblVoice";
            this.lblVoice.Size = new System.Drawing.Size(44, 19);
            this.lblVoice.TabIndex = 9;
            this.lblVoice.Text = "Voice";
            //
            // cmbVoice
            //
            this.cmbVoice.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbVoice.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.cmbVoice.FormattingEnabled = true;
            this.cmbVoice.Items.AddRange(new object[] {
            "alloy",
            "ash",
            "ballad",
            "coral",
            "echo",
            "sage",
            "shimmer",
            "verse",
            "marin",
            "cedar"});
            this.cmbVoice.Location = new System.Drawing.Point(12, 152);
            this.cmbVoice.Name = "cmbVoice";
            this.cmbVoice.Size = new System.Drawing.Size(150, 25);
            this.cmbVoice.TabIndex = 10;
            this.cmbVoice.SelectedIndex = 0;
            //
            // lblSpeed
            //
            this.lblSpeed.AutoSize = true;
            this.lblSpeed.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblSpeed.Location = new System.Drawing.Point(12, 190);
            this.lblSpeed.Name = "lblSpeed";
            this.lblSpeed.Size = new System.Drawing.Size(48, 19);
            this.lblSpeed.TabIndex = 11;
            this.lblSpeed.Text = "Speed";
            //
            // trackSpeed
            //
            this.trackSpeed.Enabled = false;
            this.trackSpeed.Location = new System.Drawing.Point(12, 212);
            this.trackSpeed.Maximum = 2;
            this.trackSpeed.Minimum = 0;
            this.trackSpeed.Name = "trackSpeed";
            this.trackSpeed.Size = new System.Drawing.Size(150, 45);
            this.trackSpeed.TabIndex = 12;
            this.trackSpeed.Value = 1;
            this.trackSpeed.ValueChanged += new System.EventHandler(this.trackSpeed_ValueChanged);

            //
            // lblLogLevel
            //
            this.lblLogLevel.AutoSize = true;
            this.lblLogLevel.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblLogLevel.Location = new System.Drawing.Point(12, 260);
            this.lblLogLevel.Name = "lblLogLevel";
            this.lblLogLevel.Size = new System.Drawing.Size(66, 19);
            this.lblLogLevel.TabIndex = 13;
            this.lblLogLevel.Text = "Log Level";
            
            //
            // cmbLogLevel
            //

            this.cmbLogLevel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbLogLevel.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.cmbLogLevel.FormattingEnabled = true;
            this.cmbLogLevel.Location = new System.Drawing.Point(12, 282);
            this.cmbLogLevel.Name = "cmbLogLevel";
            this.cmbLogLevel.Size = new System.Drawing.Size(150, 25);
            this.cmbLogLevel.TabIndex = 14;
            this.cmbLogLevel.Items.AddRange(new object[] 
            {
                "Trace",
                "Debug",
                "Information",
                "Warning",
                "Error",
                "Critical",
                "None"
            });
            this.cmbLogLevel.SelectedIndex = 6;

            //
            // lblProvider
            //
            this.lblProvider.AutoSize = true;
            this.lblProvider.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.lblProvider.Location = new System.Drawing.Point(12, 320);
            this.lblProvider.Name = "lblProvider";
            this.lblProvider.Size = new System.Drawing.Size(62, 19);
            this.lblProvider.TabIndex = 15;
            this.lblProvider.Text = "Provider";
            //
            // cmbProvider
            //
            this.cmbProvider.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbProvider.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.cmbProvider.FormattingEnabled = true;
            this.cmbProvider.Items.AddRange(new object[] {
            "OpenAI",
            "Google Gemini",
            "AWS Bedrock",
            "Vertex AI"});
            this.cmbProvider.Location = new System.Drawing.Point(12, 342);
            this.cmbProvider.Name = "cmbProvider";
            this.cmbProvider.Size = new System.Drawing.Size(150, 25);
            this.cmbProvider.TabIndex = 16;
            this.cmbProvider.SelectedIndex = 0;
            this.cmbProvider.SelectedIndexChanged += new System.EventHandler(this.cmbProvider_SelectedIndexChanged);

            //
            // chkVadEnabled
            //
            this.chkVadEnabled.AutoSize = true;
            this.chkVadEnabled.Checked = true;
            this.chkVadEnabled.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkVadEnabled.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkVadEnabled.Location = new System.Drawing.Point(12, 380);
            this.chkVadEnabled.Name = "chkVadEnabled";
            this.chkVadEnabled.Size = new System.Drawing.Size(130, 23);
            this.chkVadEnabled.TabIndex = 17;
            this.chkVadEnabled.Text = "Enable VAD";
            this.chkVadEnabled.UseVisualStyleBackColor = true;
            //
            // chkAllowInterruption
            //
            this.chkAllowInterruption.AutoSize = true;
            this.chkAllowInterruption.Checked = true;
            this.chkAllowInterruption.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAllowInterruption.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.chkAllowInterruption.Location = new System.Drawing.Point(12, 406);
            this.chkAllowInterruption.Name = "chkAllowInterruption";
            this.chkAllowInterruption.Size = new System.Drawing.Size(130, 23);
            this.chkAllowInterruption.TabIndex = 18;
            this.chkAllowInterruption.Text = "Allow Interruption";
            this.chkAllowInterruption.UseVisualStyleBackColor = true;

            //
            // statusLabel
            //
            this.statusLabel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.statusLabel.AutoSize = true;
            this.statusLabel.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.statusLabel.Location = new System.Drawing.Point(12, 436);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(150, 19);
            this.statusLabel.TabIndex = 2;
            this.statusLabel.Text = "Ready to record.";
            //
            // richTextBox2
            //
            this.richTextBox2.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.richTextBox2.Enabled = false;
            this.richTextBox2.Location = new System.Drawing.Point(175, 350);
            this.richTextBox2.Name = "richTextBox2";
            this.richTextBox2.Size = new System.Drawing.Size(367, 80);
            this.richTextBox2.TabIndex = 4;
            this.richTextBox2.Text = "";
            this.richTextBox2.KeyDown += new System.Windows.Forms.KeyEventHandler(this.richTextBox2_KeyDown);
            //
            // btnSend
            //
            this.btnSend.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnSend.Enabled = false;
            this.btnSend.Location = new System.Drawing.Point(548, 350);
            this.btnSend.Name = "btnSend";
            this.btnSend.Size = new System.Drawing.Size(70, 80);
            this.btnSend.TabIndex = 8;
            this.btnSend.Text = "Send";
            this.btnSend.UseVisualStyleBackColor = true;
            this.btnSend.Click += new System.EventHandler(this.btnSend_Click);
            //
            // splitContainer1
            //
            this.splitContainer1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.splitContainer1.Location = new System.Drawing.Point(175, 12);
            this.splitContainer1.Name = "splitContainer1";
            //
            // splitContainer1.Panel1
            //
            this.splitContainer1.Panel1.Controls.Add(this.richTextBox1);
            //
            // splitContainer1.Panel2
            //
            this.splitContainer1.Panel2.Controls.Add(this.splitContainer2);
            this.splitContainer1.Size = new System.Drawing.Size(443, 332);
            this.splitContainer1.SplitterDistance = 254;
            this.splitContainer1.TabIndex = 7;
            //
            // splitContainer2
            //
            this.splitContainer2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer2.Orientation = System.Windows.Forms.Orientation.Horizontal;
            this.splitContainer2.Name = "splitContainer2";
            //
            // splitContainer2.Panel1
            //
            this.splitContainer2.Panel1.Controls.Add(this.richTextBoxEvents);
            //
            // splitContainer2.Panel2
            //
            this.splitContainer2.Panel2.Controls.Add(this.richTextBoxLogs);
            this.splitContainer2.Size = new System.Drawing.Size(250, 250);
            this.splitContainer2.SplitterDistance = 125;
            this.splitContainer2.TabIndex = 0;
            //
            // richTextBox1
            //
            this.richTextBox1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBox1.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.richTextBox1.Location = new System.Drawing.Point(0, 0);
            this.richTextBox1.Name = "richTextBox1";
            this.richTextBox1.Size = new System.Drawing.Size(254, 250);
            this.richTextBox1.TabIndex = 0;
            this.richTextBox1.Text = "";
            //
            // richTextBoxEvents
            //
            this.richTextBoxEvents.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBoxEvents.Location = new System.Drawing.Point(0, 0);
            this.richTextBoxEvents.Name = "richTextBoxEvents";
            this.richTextBoxEvents.Size = new System.Drawing.Size(250, 125);
            this.richTextBoxEvents.TabIndex = 0;
            this.richTextBoxEvents.Text = "";
            //
            // richTextBoxLogs
            //
            this.richTextBoxLogs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.richTextBoxLogs.Font = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.richTextBoxLogs.Location = new System.Drawing.Point(0, 0);
            this.richTextBoxLogs.Name = "richTextBoxLogs";
            this.richTextBoxLogs.ReadOnly = true;
            this.richTextBoxLogs.Size = new System.Drawing.Size(250, 121);
            this.richTextBoxLogs.TabIndex = 0;
            this.richTextBoxLogs.Text = "";
            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(630, 460);
            this.Controls.Add(this.chkAllowInterruption);
            this.Controls.Add(this.chkVadEnabled);
            this.Controls.Add(this.cmbProvider);
            this.Controls.Add(this.lblProvider);
            this.Controls.Add(this.cmbLogLevel);
            this.Controls.Add(this.lblLogLevel);
            this.Controls.Add(this.trackSpeed);
            this.Controls.Add(this.lblSpeed);
            this.Controls.Add(this.cmbVoice);
            this.Controls.Add(this.lblVoice);
            this.Controls.Add(this.btnSend);
            this.Controls.Add(this.richTextBox2);
            this.Controls.Add(this.splitContainer1);
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.btnCall);
            this.Controls.Add(this.btnPlay);
            this.Controls.Add(this.btnRecord);
            this.MinimumSize = new System.Drawing.Size(450, 350);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "RealtimePlayGround - Audio Recorder";
            this.splitContainer1.Panel1.ResumeLayout(false);
            this.splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).EndInit();
            this.splitContainer1.ResumeLayout(false);
            this.splitContainer2.Panel1.ResumeLayout(false);
            this.splitContainer2.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).EndInit();
            this.splitContainer2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.trackSpeed)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
