<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class ControlPanel
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(ControlPanel))
        Me.AddReminder = New System.Windows.Forms.Button()
        Me.PictureBox1 = New System.Windows.Forms.PictureBox()
        Me.Label1 = New System.Windows.Forms.Label()
        Me.Label2 = New System.Windows.Forms.Label()
        Me.PictureBox2 = New System.Windows.Forms.PictureBox()
        Me.Label3 = New System.Windows.Forms.Label()
        Me.Label4 = New System.Windows.Forms.Label()
        Me.Label5 = New System.Windows.Forms.Label()
        Me.DateI = New System.Windows.Forms.DateTimePicker()
        Me.TimeI = New System.Windows.Forms.DateTimePicker()
        Me.ReminderI = New System.Windows.Forms.TextBox()
        Me.RemindersT = New System.Windows.Forms.TextBox()
        Me.PictureBox3 = New System.Windows.Forms.PictureBox()
        Me.Label6 = New System.Windows.Forms.Label()
        Me.InternetI = New System.Windows.Forms.PictureBox()
        Me.InternetT = New System.Windows.Forms.Label()
        Me.InternetN = New System.Windows.Forms.PictureBox()
        Me.HideBTN = New System.Windows.Forms.Button()
        Me.SettingsBTN = New System.Windows.Forms.Button()
        Me.ReminderBColourS = New System.Windows.Forms.ColorDialog()
        Me.ChangeRminderBBTN = New System.Windows.Forms.Button()
        Me.SettingsPanel = New System.Windows.Forms.GroupBox()
        Me.Label8 = New System.Windows.Forms.Label()
        Me.TimeF = New System.Windows.Forms.ComboBox()
        Me.VersionL = New System.Windows.Forms.Label()
        Me.ReminderTColour = New System.Windows.Forms.PictureBox()
        Me.ChangeRminderTBTN = New System.Windows.Forms.Button()
        Me.Label7 = New System.Windows.Forms.Label()
        Me.CurrentPC = New System.Windows.Forms.Label()
        Me.CurrentPCL = New System.Windows.Forms.Label()
        Me.RemovePCName = New System.Windows.Forms.Button()
        Me.LogoutBTN = New System.Windows.Forms.Button()
        Me.ExitBTN = New System.Windows.Forms.Button()
        Me.SettingsL = New System.Windows.Forms.Label()
        Me.ReminderBColour = New System.Windows.Forms.PictureBox()
        Me.ReminderTColourS = New System.Windows.Forms.ColorDialog()
        CType(Me.PictureBox1, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.PictureBox2, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.PictureBox3, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.InternetI, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.InternetN, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SettingsPanel.SuspendLayout()
        CType(Me.ReminderTColour, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.ReminderBColour, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'AddReminder
        '
        Me.AddReminder.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.AddReminder.FlatStyle = System.Windows.Forms.FlatStyle.Flat
        Me.AddReminder.Font = New System.Drawing.Font("Calibri", 15.75!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.AddReminder.ForeColor = System.Drawing.Color.White
        Me.AddReminder.Location = New System.Drawing.Point(364, 323)
        Me.AddReminder.Name = "AddReminder"
        Me.AddReminder.Size = New System.Drawing.Size(105, 41)
        Me.AddReminder.TabIndex = 17
        Me.AddReminder.Text = "Add"
        Me.AddReminder.UseVisualStyleBackColor = False
        '
        'PictureBox1
        '
        Me.PictureBox1.BackColor = System.Drawing.Color.White
        Me.PictureBox1.Location = New System.Drawing.Point(46, 98)
        Me.PictureBox1.Name = "PictureBox1"
        Me.PictureBox1.Size = New System.Drawing.Size(234, 282)
        Me.PictureBox1.TabIndex = 1
        Me.PictureBox1.TabStop = False
        '
        'Label1
        '
        Me.Label1.AutoSize = True
        Me.Label1.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.Label1.Font = New System.Drawing.Font("Microsoft Sans Serif", 21.75!, System.Drawing.FontStyle.Underline, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.Label1.ForeColor = System.Drawing.Color.White
        Me.Label1.Location = New System.Drawing.Point(39, 53)
        Me.Label1.Name = "Label1"
        Me.Label1.Size = New System.Drawing.Size(157, 33)
        Me.Label1.TabIndex = 2
        Me.Label1.Text = "Reminders"
        '
        'Label2
        '
        Me.Label2.AutoSize = True
        Me.Label2.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.Label2.Font = New System.Drawing.Font("Microsoft Sans Serif", 21.75!, System.Drawing.FontStyle.Underline, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.Label2.ForeColor = System.Drawing.Color.White
        Me.Label2.Location = New System.Drawing.Point(297, 53)
        Me.Label2.Name = "Label2"
        Me.Label2.Size = New System.Drawing.Size(201, 33)
        Me.Label2.TabIndex = 4
        Me.Label2.Text = "Add Reminder"
        '
        'PictureBox2
        '
        Me.PictureBox2.BackColor = System.Drawing.Color.White
        Me.PictureBox2.Location = New System.Drawing.Point(304, 98)
        Me.PictureBox2.Name = "PictureBox2"
        Me.PictureBox2.Size = New System.Drawing.Size(234, 282)
        Me.PictureBox2.TabIndex = 3
        Me.PictureBox2.TabStop = False
        '
        'Label3
        '
        Me.Label3.AutoSize = True
        Me.Label3.BackColor = System.Drawing.Color.White
        Me.Label3.Font = New System.Drawing.Font("Calibri", 12.0!)
        Me.Label3.Location = New System.Drawing.Point(330, 113)
        Me.Label3.Name = "Label3"
        Me.Label3.Size = New System.Drawing.Size(71, 19)
        Me.Label3.TabIndex = 8
        Me.Label3.Text = "Reminder"
        '
        'Label4
        '
        Me.Label4.AutoSize = True
        Me.Label4.BackColor = System.Drawing.Color.White
        Me.Label4.Font = New System.Drawing.Font("Calibri", 12.0!)
        Me.Label4.Location = New System.Drawing.Point(330, 179)
        Me.Label4.Name = "Label4"
        Me.Label4.Size = New System.Drawing.Size(41, 19)
        Me.Label4.TabIndex = 9
        Me.Label4.Text = "Time"
        '
        'Label5
        '
        Me.Label5.AutoSize = True
        Me.Label5.BackColor = System.Drawing.Color.White
        Me.Label5.Font = New System.Drawing.Font("Calibri", 12.0!)
        Me.Label5.Location = New System.Drawing.Point(330, 245)
        Me.Label5.Name = "Label5"
        Me.Label5.Size = New System.Drawing.Size(40, 19)
        Me.Label5.TabIndex = 10
        Me.Label5.Text = "Date"
        '
        'DateI
        '
        Me.DateI.Location = New System.Drawing.Point(334, 281)
        Me.DateI.Name = "DateI"
        Me.DateI.Size = New System.Drawing.Size(170, 21)
        Me.DateI.TabIndex = 16
        '
        'TimeI
        '
        Me.TimeI.Checked = False
        Me.TimeI.Location = New System.Drawing.Point(334, 218)
        Me.TimeI.Name = "TimeI"
        Me.TimeI.Size = New System.Drawing.Size(171, 21)
        Me.TimeI.TabIndex = 15
        '
        'ReminderI
        '
        Me.ReminderI.Font = New System.Drawing.Font("Calibri", 14.0!)
        Me.ReminderI.Location = New System.Drawing.Point(334, 141)
        Me.ReminderI.Name = "ReminderI"
        Me.ReminderI.Size = New System.Drawing.Size(170, 30)
        Me.ReminderI.TabIndex = 14
        '
        'RemindersT
        '
        Me.RemindersT.Location = New System.Drawing.Point(58, 121)
        Me.RemindersT.Multiline = True
        Me.RemindersT.Name = "RemindersT"
        Me.RemindersT.ScrollBars = System.Windows.Forms.ScrollBars.Both
        Me.RemindersT.Size = New System.Drawing.Size(208, 243)
        Me.RemindersT.TabIndex = 0
        Me.RemindersT.TabStop = False
        '
        'PictureBox3
        '
        Me.PictureBox3.BackColor = System.Drawing.Color.White
        Me.PictureBox3.Location = New System.Drawing.Point(0, -1)
        Me.PictureBox3.Name = "PictureBox3"
        Me.PictureBox3.Size = New System.Drawing.Size(761, 51)
        Me.PictureBox3.TabIndex = 16
        Me.PictureBox3.TabStop = False
        '
        'Label6
        '
        Me.Label6.BackColor = System.Drawing.Color.White
        Me.Label6.Font = New System.Drawing.Font("Calibri", 27.75!, CType((System.Drawing.FontStyle.Bold Or System.Drawing.FontStyle.Underline), System.Drawing.FontStyle), System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.Label6.ForeColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.Label6.Location = New System.Drawing.Point(310, 0)
        Me.Label6.Name = "Label6"
        Me.Label6.Size = New System.Drawing.Size(156, 46)
        Me.Label6.TabIndex = 0
        Me.Label6.Text = "PCClient"
        '
        'InternetI
        '
        Me.InternetI.Image = CType(resources.GetObject("InternetI.Image"), System.Drawing.Image)
        Me.InternetI.Location = New System.Drawing.Point(674, 68)
        Me.InternetI.Name = "InternetI"
        Me.InternetI.Size = New System.Drawing.Size(76, 64)
        Me.InternetI.TabIndex = 17
        Me.InternetI.TabStop = False
        '
        'InternetT
        '
        Me.InternetT.AutoSize = True
        Me.InternetT.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.InternetT.ForeColor = System.Drawing.Color.White
        Me.InternetT.Location = New System.Drawing.Point(554, 89)
        Me.InternetT.Name = "InternetT"
        Me.InternetT.Size = New System.Drawing.Size(124, 23)
        Me.InternetT.TabIndex = 18
        Me.InternetT.Text = "Not Connected"
        '
        'InternetN
        '
        Me.InternetN.Image = CType(resources.GetObject("InternetN.Image"), System.Drawing.Image)
        Me.InternetN.Location = New System.Drawing.Point(674, 68)
        Me.InternetN.Name = "InternetN"
        Me.InternetN.Size = New System.Drawing.Size(76, 64)
        Me.InternetN.TabIndex = 20
        Me.InternetN.TabStop = False
        Me.InternetN.Visible = False
        '
        'HideBTN
        '
        Me.HideBTN.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.HideBTN.Location = New System.Drawing.Point(638, 347)
        Me.HideBTN.Name = "HideBTN"
        Me.HideBTN.Size = New System.Drawing.Size(112, 43)
        Me.HideBTN.TabIndex = 21
        Me.HideBTN.Text = "Hide"
        Me.HideBTN.UseVisualStyleBackColor = True
        '
        'SettingsBTN
        '
        Me.SettingsBTN.BackColor = System.Drawing.Color.White
        Me.SettingsBTN.BackgroundImage = CType(resources.GetObject("SettingsBTN.BackgroundImage"), System.Drawing.Image)
        Me.SettingsBTN.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Zoom
        Me.SettingsBTN.FlatStyle = System.Windows.Forms.FlatStyle.Flat
        Me.SettingsBTN.ForeColor = System.Drawing.Color.White
        Me.SettingsBTN.Location = New System.Drawing.Point(692, 1)
        Me.SettingsBTN.Name = "SettingsBTN"
        Me.SettingsBTN.Size = New System.Drawing.Size(69, 49)
        Me.SettingsBTN.TabIndex = 22
        Me.SettingsBTN.UseVisualStyleBackColor = False
        '
        'ChangeRminderBBTN
        '
        Me.ChangeRminderBBTN.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.ChangeRminderBBTN.Font = New System.Drawing.Font("Calibri", 9.75!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.ChangeRminderBBTN.ForeColor = System.Drawing.Color.White
        Me.ChangeRminderBBTN.Location = New System.Drawing.Point(626, 55)
        Me.ChangeRminderBBTN.Name = "ChangeRminderBBTN"
        Me.ChangeRminderBBTN.Size = New System.Drawing.Size(129, 43)
        Me.ChangeRminderBBTN.TabIndex = 24
        Me.ChangeRminderBBTN.Text = "Change Reminder Background Colour"
        Me.ChangeRminderBBTN.UseVisualStyleBackColor = False
        '
        'SettingsPanel
        '
        Me.SettingsPanel.BackColor = System.Drawing.SystemColors.Window
        Me.SettingsPanel.Controls.Add(Me.Label8)
        Me.SettingsPanel.Controls.Add(Me.TimeF)
        Me.SettingsPanel.Controls.Add(Me.VersionL)
        Me.SettingsPanel.Controls.Add(Me.ReminderTColour)
        Me.SettingsPanel.Controls.Add(Me.ChangeRminderTBTN)
        Me.SettingsPanel.Controls.Add(Me.Label7)
        Me.SettingsPanel.Controls.Add(Me.CurrentPC)
        Me.SettingsPanel.Controls.Add(Me.CurrentPCL)
        Me.SettingsPanel.Controls.Add(Me.RemovePCName)
        Me.SettingsPanel.Controls.Add(Me.LogoutBTN)
        Me.SettingsPanel.Controls.Add(Me.ExitBTN)
        Me.SettingsPanel.Controls.Add(Me.SettingsL)
        Me.SettingsPanel.Controls.Add(Me.ReminderBColour)
        Me.SettingsPanel.Controls.Add(Me.ChangeRminderBBTN)
        Me.SettingsPanel.ForeColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.SettingsPanel.Location = New System.Drawing.Point(0, 49)
        Me.SettingsPanel.Name = "SettingsPanel"
        Me.SettingsPanel.Size = New System.Drawing.Size(761, 353)
        Me.SettingsPanel.TabIndex = 25
        Me.SettingsPanel.TabStop = False
        Me.SettingsPanel.Text = "Settings"
        Me.SettingsPanel.Visible = False
        '
        'Label8
        '
        Me.Label8.AutoSize = True
        Me.Label8.Font = New System.Drawing.Font("Calibri", 9.75!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.Label8.Location = New System.Drawing.Point(543, 165)
        Me.Label8.Name = "Label8"
        Me.Label8.Size = New System.Drawing.Size(75, 15)
        Me.Label8.TabIndex = 37
        Me.Label8.Text = "Time Format"
        '
        'TimeF
        '
        Me.TimeF.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.TimeF.FormattingEnabled = True
        Me.TimeF.Items.AddRange(New Object() {"24 Hour", "12 Hour"})
        Me.TimeF.Location = New System.Drawing.Point(629, 164)
        Me.TimeF.Name = "TimeF"
        Me.TimeF.Size = New System.Drawing.Size(121, 21)
        Me.TimeF.TabIndex = 36
        '
        'VersionL
        '
        Me.VersionL.AutoSize = True
        Me.VersionL.Font = New System.Drawing.Font("Calibri", 12.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.VersionL.Location = New System.Drawing.Point(602, 322)
        Me.VersionL.Name = "VersionL"
        Me.VersionL.Size = New System.Drawing.Size(65, 19)
        Me.VersionL.TabIndex = 35
        Me.VersionL.Text = "Version: "
        '
        'ReminderTColour
        '
        Me.ReminderTColour.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle
        Me.ReminderTColour.Location = New System.Drawing.Point(546, 116)
        Me.ReminderTColour.Name = "ReminderTColour"
        Me.ReminderTColour.Size = New System.Drawing.Size(74, 24)
        Me.ReminderTColour.TabIndex = 34
        Me.ReminderTColour.TabStop = False
        '
        'ChangeRminderTBTN
        '
        Me.ChangeRminderTBTN.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.ChangeRminderTBTN.Font = New System.Drawing.Font("Calibri", 9.75!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.ChangeRminderTBTN.ForeColor = System.Drawing.Color.White
        Me.ChangeRminderTBTN.Location = New System.Drawing.Point(626, 106)
        Me.ChangeRminderTBTN.Name = "ChangeRminderTBTN"
        Me.ChangeRminderTBTN.Size = New System.Drawing.Size(129, 43)
        Me.ChangeRminderTBTN.TabIndex = 33
        Me.ChangeRminderTBTN.Text = "Change Reminder Text Colour"
        Me.ChangeRminderTBTN.UseVisualStyleBackColor = False
        '
        'Label7
        '
        Me.Label7.AutoSize = True
        Me.Label7.Font = New System.Drawing.Font("Calibri", 15.75!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.Label7.Location = New System.Drawing.Point(294, 315)
        Me.Label7.Name = "Label7"
        Me.Label7.Size = New System.Drawing.Size(187, 26)
        Me.Label7.TabIndex = 32
        Me.Label7.Text = "© All rights reserved"
        '
        'CurrentPC
        '
        Me.CurrentPC.AutoSize = True
        Me.CurrentPC.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.CurrentPC.Location = New System.Drawing.Point(110, 164)
        Me.CurrentPC.Name = "CurrentPC"
        Me.CurrentPC.Size = New System.Drawing.Size(0, 23)
        Me.CurrentPC.TabIndex = 31
        '
        'CurrentPCL
        '
        Me.CurrentPCL.AutoSize = True
        Me.CurrentPCL.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.CurrentPCL.Location = New System.Drawing.Point(12, 164)
        Me.CurrentPCL.Name = "CurrentPCL"
        Me.CurrentPCL.Size = New System.Drawing.Size(98, 23)
        Me.CurrentPCL.TabIndex = 30
        Me.CurrentPCL.Text = "Current PC:"
        '
        'RemovePCName
        '
        Me.RemovePCName.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.RemovePCName.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.RemovePCName.ForeColor = System.Drawing.Color.White
        Me.RemovePCName.Location = New System.Drawing.Point(12, 190)
        Me.RemovePCName.Name = "RemovePCName"
        Me.RemovePCName.Size = New System.Drawing.Size(112, 43)
        Me.RemovePCName.TabIndex = 29
        Me.RemovePCName.Text = "Unlink PC"
        Me.RemovePCName.UseVisualStyleBackColor = False
        '
        'LogoutBTN
        '
        Me.LogoutBTN.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.LogoutBTN.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.LogoutBTN.ForeColor = System.Drawing.Color.White
        Me.LogoutBTN.Location = New System.Drawing.Point(12, 239)
        Me.LogoutBTN.Name = "LogoutBTN"
        Me.LogoutBTN.Size = New System.Drawing.Size(112, 43)
        Me.LogoutBTN.TabIndex = 27
        Me.LogoutBTN.Text = "Logout"
        Me.LogoutBTN.UseVisualStyleBackColor = False
        '
        'ExitBTN
        '
        Me.ExitBTN.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.ExitBTN.Font = New System.Drawing.Font("Calibri", 14.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.ExitBTN.ForeColor = System.Drawing.Color.White
        Me.ExitBTN.Location = New System.Drawing.Point(12, 288)
        Me.ExitBTN.Name = "ExitBTN"
        Me.ExitBTN.Size = New System.Drawing.Size(112, 43)
        Me.ExitBTN.TabIndex = 28
        Me.ExitBTN.Text = "Exit"
        Me.ExitBTN.UseVisualStyleBackColor = False
        '
        'SettingsL
        '
        Me.SettingsL.AutoSize = True
        Me.SettingsL.Font = New System.Drawing.Font("Calibri", 24.0!, System.Drawing.FontStyle.Underline, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.SettingsL.Location = New System.Drawing.Point(328, 17)
        Me.SettingsL.Name = "SettingsL"
        Me.SettingsL.Size = New System.Drawing.Size(120, 39)
        Me.SettingsL.TabIndex = 26
        Me.SettingsL.Text = "Settings"
        '
        'ReminderBColour
        '
        Me.ReminderBColour.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle
        Me.ReminderBColour.Location = New System.Drawing.Point(546, 64)
        Me.ReminderBColour.Name = "ReminderBColour"
        Me.ReminderBColour.Size = New System.Drawing.Size(74, 24)
        Me.ReminderBColour.TabIndex = 25
        Me.ReminderBColour.TabStop = False
        '
        'ControlPanel
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.BackColor = System.Drawing.Color.FromArgb(CType(CType(3, Byte), Integer), CType(CType(37, Byte), Integer), CType(CType(59, Byte), Integer))
        Me.ClientSize = New System.Drawing.Size(760, 402)
        Me.Controls.Add(Me.SettingsPanel)
        Me.Controls.Add(Me.SettingsBTN)
        Me.Controls.Add(Me.HideBTN)
        Me.Controls.Add(Me.InternetN)
        Me.Controls.Add(Me.InternetT)
        Me.Controls.Add(Me.InternetI)
        Me.Controls.Add(Me.Label6)
        Me.Controls.Add(Me.PictureBox3)
        Me.Controls.Add(Me.RemindersT)
        Me.Controls.Add(Me.ReminderI)
        Me.Controls.Add(Me.TimeI)
        Me.Controls.Add(Me.DateI)
        Me.Controls.Add(Me.AddReminder)
        Me.Controls.Add(Me.Label5)
        Me.Controls.Add(Me.Label4)
        Me.Controls.Add(Me.Label3)
        Me.Controls.Add(Me.Label2)
        Me.Controls.Add(Me.PictureBox2)
        Me.Controls.Add(Me.Label1)
        Me.Controls.Add(Me.PictureBox1)
        Me.Font = New System.Drawing.Font("Calibri", 8.25!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle
        Me.Icon = CType(resources.GetObject("$this.Icon"), System.Drawing.Icon)
        Me.MaximizeBox = False
        Me.Name = "ControlPanel"
        Me.Text = "Control Panel"
        CType(Me.PictureBox1, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.PictureBox2, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.PictureBox3, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.InternetI, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.InternetN, System.ComponentModel.ISupportInitialize).EndInit()
        Me.SettingsPanel.ResumeLayout(False)
        Me.SettingsPanel.PerformLayout()
        CType(Me.ReminderTColour, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.ReminderBColour, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents AddReminder As Button
    Friend WithEvents PictureBox1 As PictureBox
    Friend WithEvents Label1 As Label
    Friend WithEvents Label2 As Label
    Friend WithEvents PictureBox2 As PictureBox
    Friend WithEvents Label3 As Label
    Friend WithEvents Label4 As Label
    Friend WithEvents Label5 As Label
    Friend WithEvents DateI As DateTimePicker
    Friend WithEvents TimeI As DateTimePicker
    Friend WithEvents ReminderI As TextBox
    Friend WithEvents RemindersT As TextBox
    Friend WithEvents PictureBox3 As PictureBox
    Friend WithEvents Label6 As Label
    Friend WithEvents InternetI As PictureBox
    Friend WithEvents InternetT As Label
    Friend WithEvents InternetN As PictureBox
    Friend WithEvents HideBTN As Button
    Friend WithEvents SettingsBTN As Button
    Friend WithEvents ReminderBColourS As ColorDialog
    Friend WithEvents ChangeRminderBBTN As Button
    Friend WithEvents SettingsPanel As GroupBox
    Friend WithEvents ReminderBColour As PictureBox
    Friend WithEvents SettingsL As Label
    Friend WithEvents LogoutBTN As Button
    Friend WithEvents ExitBTN As Button
    Friend WithEvents RemovePCName As Button
    Friend WithEvents CurrentPCL As Label
    Friend WithEvents CurrentPC As Label
    Friend WithEvents Label7 As Label
    Friend WithEvents ReminderTColour As PictureBox
    Friend WithEvents ChangeRminderTBTN As Button
    Friend WithEvents ReminderTColourS As ColorDialog
    Friend WithEvents VersionL As Label
    Friend WithEvents TimeF As ComboBox
    Friend WithEvents Label8 As Label
End Class
