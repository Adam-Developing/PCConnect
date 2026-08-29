<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class AddPC
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
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
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(AddPC))
        Me.AddPCI = New System.Windows.Forms.TextBox()
        Me.PCNameL = New System.Windows.Forms.Label()
        Me.AddPCB = New System.Windows.Forms.Button()
        Me.Label1 = New System.Windows.Forms.Label()
        Me.ExistingPCName = New System.Windows.Forms.ComboBox()
        Me.UseExistingC = New System.Windows.Forms.CheckBox()
        Me.SuspendLayout()
        '
        'AddPCI
        '
        Me.AddPCI.Location = New System.Drawing.Point(122, 46)
        Me.AddPCI.Name = "AddPCI"
        Me.AddPCI.Size = New System.Drawing.Size(105, 20)
        Me.AddPCI.TabIndex = 0
        '
        'PCNameL
        '
        Me.PCNameL.AutoSize = True
        Me.PCNameL.Location = New System.Drawing.Point(61, 46)
        Me.PCNameL.Name = "PCNameL"
        Me.PCNameL.Size = New System.Drawing.Size(55, 13)
        Me.PCNameL.TabIndex = 1
        Me.PCNameL.Text = "PC Name:"
        '
        'AddPCB
        '
        Me.AddPCB.Location = New System.Drawing.Point(100, 74)
        Me.AddPCB.Name = "AddPCB"
        Me.AddPCB.Size = New System.Drawing.Size(75, 23)
        Me.AddPCB.TabIndex = 2
        Me.AddPCB.Text = "Add PC"
        Me.AddPCB.UseVisualStyleBackColor = True
        '
        'Label1
        '
        Me.Label1.AutoSize = True
        Me.Label1.Font = New System.Drawing.Font("Microsoft Sans Serif", 9.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.Label1.Location = New System.Drawing.Point(12, 15)
        Me.Label1.Name = "Label1"
        Me.Label1.Size = New System.Drawing.Size(246, 15)
        Me.Label1.TabIndex = 3
        Me.Label1.Text = "Please provide this PC a name to identify by."
        '
        'ExistingPCName
        '
        Me.ExistingPCName.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.ExistingPCName.Enabled = False
        Me.ExistingPCName.FormattingEnabled = True
        Me.ExistingPCName.Location = New System.Drawing.Point(122, 45)
        Me.ExistingPCName.Name = "ExistingPCName"
        Me.ExistingPCName.Size = New System.Drawing.Size(105, 21)
        Me.ExistingPCName.TabIndex = 4
        Me.ExistingPCName.Visible = False
        '
        'UseExistingC
        '
        Me.UseExistingC.AutoSize = True
        Me.UseExistingC.Checked = True
        Me.UseExistingC.CheckState = System.Windows.Forms.CheckState.Checked
        Me.UseExistingC.Location = New System.Drawing.Point(12, 88)
        Me.UseExistingC.Name = "UseExistingC"
        Me.UseExistingC.Size = New System.Drawing.Size(84, 17)
        Me.UseExistingC.TabIndex = 6
        Me.UseExistingC.Text = "Use Existing"
        Me.UseExistingC.UseVisualStyleBackColor = True
        '
        'AddPC
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(277, 115)
        Me.Controls.Add(Me.UseExistingC)
        Me.Controls.Add(Me.ExistingPCName)
        Me.Controls.Add(Me.Label1)
        Me.Controls.Add(Me.AddPCB)
        Me.Controls.Add(Me.PCNameL)
        Me.Controls.Add(Me.AddPCI)
        Me.Icon = CType(resources.GetObject("$this.Icon"), System.Drawing.Icon)
        Me.MaximizeBox = False
        Me.Name = "AddPC"
        Me.Text = "Add PC"
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents AddPCI As TextBox
    Friend WithEvents PCNameL As Label
    Friend WithEvents AddPCB As Button
    Friend WithEvents Label1 As Label
    Friend WithEvents ExistingPCName As ComboBox
    Friend WithEvents UseExistingC As CheckBox
End Class
