Imports System.Net.Http

Public Class ControlPanel
    Dim ReminderURL As String = "https://pcconnect.adamkhattab.co.uk/api/pcclient/reminder.php"
    Dim SettingsCheck As Integer = 0
    Public Internet As Integer = 0

    Private Sub ControlPanel_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        TimeF.Text = My.Settings.TimeFormat
        TimeI.Format = DateTimePickerFormat.Custom

        If TimeF.Text = "24 Hour" Then
            TimeI.CustomFormat = "HH:mm"
        ElseIf TimeF.Text = "12 Hour" Then
            TimeI.CustomFormat = "hh:mm tt"
        End If

        TimeI.ShowUpDown = True
        DateI.Format = DateTimePickerFormat.Short

        RemindersT.ReadOnly = True
        RemindersT.Text = ""
        VersionL.Text = "Version: " & Me.ProductVersion

    End Sub
    Function RemindersTextEdit(ReminderListTextBox)
        RemindersT.Text = ReminderListTextBox
    End Function

    Public Function internetChange(value)
        If value = 0 Then
            Internet = 1
            InternetT.Text = "Not Connected"
            InternetI.Hide()
            InternetN.Show()
        ElseIf value = 1 Then
            Internet = 0
            InternetT.Text = "Connected"
            InternetI.Show()
            InternetN.Hide()

        Else
            Internet = 0
            InternetT.Text = "There was an error when checking the internet connection."
            InternetI.Hide()
            InternetN.Show()

        End If
    End Function
    Async Function AddReminderFunction() As Task
        Using httpClient As New HttpClient()
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", PCClient.apiKey)
            Try


                Dim ReminderPostData As New List(Of KeyValuePair(Of String, String))

                ReminderPostData.Add(New KeyValuePair(Of String, String)("date", DateI.Text))
                ReminderPostData.Add(New KeyValuePair(Of String, String)("time", TimeI.Text))
                ReminderPostData.Add(New KeyValuePair(Of String, String)("reminder", ReminderI.Text))

                Dim ReminderContent As New FormUrlEncodedContent(ReminderPostData)
                Dim ReminderResponse As HttpResponseMessage = Await httpClient.PostAsync(ReminderURL, ReminderContent)
                Dim LoginResponseContent As String = ReminderResponse.Content.ReadAsStringAsync().Result
                If LoginResponseContent = "Reminder inserted successfully!" & vbLf Then

                    MsgBox("Success")

                    ReminderI.Text = ""
                    DateI.Text = ""
                    TimeI.Text = ""
                Else
                    MsgBox(LoginResponseContent)
                End If

            Catch ex As Exception

                MsgBox("Error: " & ex.Message)


            End Try

            ' Add a delay to avoid excessive requests
            Await Task.Delay(TimeSpan.FromSeconds(0)) ' Adjust the delay interval as needed
        End Using


    End Function

    Private Sub AddReminder_Click(sender As Object, e As EventArgs) Handles AddReminder.Click
        AddReminderFunction()
    End Sub

    Private Sub ExitBTN_Click(sender As Object, e As EventArgs) Handles ExitBTN.Click
        Application.Exit()
    End Sub

    Private Sub LogoutBTN_Click(sender As Object, e As EventArgs) Handles LogoutBTN.Click
        PCClient.logoutPCClient()
    End Sub

    Private Sub HideBTN_Click(sender As Object, e As EventArgs) Handles HideBTN.Click
        Me.Close()
    End Sub

    Private Sub SettingsBTN_Click(sender As Object, e As EventArgs) Handles SettingsBTN.Click
        If SettingsCheck = 0 Then
            ReminderBColour.BackColor = My.Settings.ReminderBColour
            ReminderTColour.BackColor = My.Settings.ReminderTColour

            CurrentPC.Text = PCClient.PCName
            SettingsCheck = 1
            SettingsPanel.Visible = True

        Else
            SettingsCheck = 0
            SettingsPanel.Visible = False
        End If

    End Sub

    Private Sub ChangeRminderBBTN_Click(sender As Object, e As EventArgs) Handles ChangeRminderBBTN.Click
        ReminderBColourS.ShowDialog()
        My.Settings.ReminderBColour = ReminderBColourS.Color

        My.Settings.Save()
        ReminderBColour.BackColor = ReminderBColourS.Color
    End Sub

    Private Sub RemovePCName_Click(sender As Object, e As EventArgs) Handles RemovePCName.Click
        PCClient.PCName = ""
        My.Settings.PCName = ""
        My.Settings.Save()
        PCClient.ControlPanelItem.Visible = False

        Me.Close()
        PCClient.Running()
    End Sub

    Private Sub ChangeRminderTBTN_Click(sender As Object, e As EventArgs) Handles ChangeRminderTBTN.Click
        ReminderTColourS.ShowDialog()
        My.Settings.ReminderTColour = ReminderTColourS.Color

        My.Settings.Save()
        ReminderTColour.BackColor = ReminderTColourS.Color
    End Sub

    Private Sub TimeF_SelectedIndexChanged(sender As Object, e As EventArgs) Handles TimeF.SelectedIndexChanged

        My.Settings.TimeFormat = TimeF.Text
        My.Settings.Save()
        If TimeF.Text = "24 Hour" Then
            TimeI.CustomFormat = "HH:mm"
        ElseIf TimeF.Text = "12 Hour" Then
            TimeI.CustomFormat = "hh:mm tt"
        End If
    End Sub


End Class