<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class Login
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
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(Login))
        Me.UsernameI = New System.Windows.Forms.TextBox()
        Me.UsernameL = New System.Windows.Forms.Label()
        Me.PaswordL = New System.Windows.Forms.Label()
        Me.PasswordI = New System.Windows.Forms.TextBox()
        Me.SubmitB = New System.Windows.Forms.Button()
        Me.SuspendLayout()
        '
        'UsernameI
        '
        Me.UsernameI.AcceptsTab = True
        Me.UsernameI.Location = New System.Drawing.Point(112, 20)
        Me.UsernameI.Name = "UsernameI"
        Me.UsernameI.Size = New System.Drawing.Size(138, 20)
        Me.UsernameI.TabIndex = 0
        '
        'UsernameL
        '
        Me.UsernameL.AutoSize = True
        Me.UsernameL.Location = New System.Drawing.Point(9, 27)
        Me.UsernameL.Name = "UsernameL"
        Me.UsernameL.Size = New System.Drawing.Size(95, 13)
        Me.UsernameL.TabIndex = 1
        Me.UsernameL.Text = "Username or Email"
        '
        'PaswordL
        '
        Me.PaswordL.AutoSize = True
        Me.PaswordL.Location = New System.Drawing.Point(53, 49)
        Me.PaswordL.Name = "PaswordL"
        Me.PaswordL.Size = New System.Drawing.Size(53, 13)
        Me.PaswordL.TabIndex = 3
        Me.PaswordL.Text = "Password"
        '
        'PasswordI
        '
        Me.PasswordI.AcceptsTab = True
        Me.PasswordI.Location = New System.Drawing.Point(112, 46)
        Me.PasswordI.Name = "PasswordI"
        Me.PasswordI.Size = New System.Drawing.Size(138, 20)
        Me.PasswordI.TabIndex = 2
        Me.PasswordI.UseSystemPasswordChar = True
        '
        'SubmitB
        '
        Me.SubmitB.AutoEllipsis = True
        Me.SubmitB.Location = New System.Drawing.Point(103, 79)
        Me.SubmitB.Name = "SubmitB"
        Me.SubmitB.Size = New System.Drawing.Size(99, 30)
        Me.SubmitB.TabIndex = 4
        Me.SubmitB.Text = "Submit"
        Me.SubmitB.UseVisualStyleBackColor = True
        '
        'Login
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 13.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(276, 124)
        Me.Controls.Add(Me.SubmitB)
        Me.Controls.Add(Me.PaswordL)
        Me.Controls.Add(Me.PasswordI)
        Me.Controls.Add(Me.UsernameL)
        Me.Controls.Add(Me.UsernameI)
        Me.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle
        Me.Icon = CType(resources.GetObject("$this.Icon"), System.Drawing.Icon)
        Me.MaximizeBox = False
        Me.Name = "Login"
        Me.Text = "Login"
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub

    Friend WithEvents UsernameI As TextBox
    Friend WithEvents UsernameL As Label
    Friend WithEvents PaswordL As Label
    Friend WithEvents PasswordI As TextBox
    Friend WithEvents SubmitB As Button
End Class
