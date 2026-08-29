Imports System.Net.Http
Imports System.Runtime.InteropServices
Imports System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock


Public Class reminderWindow
    Dim reminderTextWindow As String
    Dim IDWindow As String
    Dim CompleteReminder As String = "https://pcconnect.adamkhattab.co.uk/api/pcclient/completereminder.php"
    Dim screenWidth As Integer = Screen.PrimaryScreen.Bounds.Width
    Dim screenHeight As Integer = Screen.PrimaryScreen.Bounds.Height


    ' Usage:
    ' To block the keyboard:
    ' KeyboardBlocker.BlockKeyboard()

    ' To unblock the keyboard:
    ' KeyboardBlocker.UnblockKeyboard()


    Async Function Running(reminderText, ID) As Task

        reminderTextWindow = reminderText
        IDWindow = ID
        Me.Show()
    End Function
    Async Function Complete() As Task
        KeyboardBlocker.UnblockKeyboard()
        Me.Close()

        Using httpClient As New HttpClient()

            httpClient.DefaultRequestHeaders.Add("X-API-KEY", PCClient.apiKey)
            Dim ReminderPostData As New List(Of KeyValuePair(Of String, String))
            ReminderPostData.Add(New KeyValuePair(Of String, String)("id", IDWindow))
            Dim ReminderContent As New FormUrlEncodedContent(ReminderPostData)

            Dim TimeResponse As HttpResponseMessage = Await httpClient.PostAsync(CompleteReminder, ReminderContent)

        End Using


    End Function


    Private Sub reminder_Load(sender As Object, e As EventArgs) Handles MyBase.Load

        KeyboardBlocker.BlockKeyboard()

        ReminderTextL.Text = reminderTextWindow
        ReminderTextL.Location = New Point((screenWidth - ReminderTextL.Width) / 2, (screenHeight - ReminderTextL.Height) / 2)
        DismissB.Location = New Point((screenWidth - DismissB.Width) / 2, ((screenHeight - DismissB.Height) / 2) + 100)
        Me.BackColor = My.Settings.ReminderBColour
        ReminderTextL.ForeColor = My.Settings.ReminderTColour
        Me.TopMost = True

    End Sub

    Private Sub DismissB_Click(sender As Object, e As EventArgs) Handles DismissB.Click
        Complete()
    End Sub
End Class