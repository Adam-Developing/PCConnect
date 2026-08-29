Imports System.Net.Http
Imports Newtonsoft.Json




Public Class AddPC
    Dim addPCApiUrl As String = "https://pcconnect.adamkhattab.co.uk/api/pcclient/addpc.php" ' Replace with the actual URL of your PHP script
    Dim GetPCApiUrl As String = "https://pcconnect.adamkhattab.co.uk/api/pcclient/PCNames.php" ' Replace with the actual URL of your PHP script
    Dim PCName As String
    Public PCNames As List(Of String)
    Public Class PCNamesResponse
        Public Property PCNames As List(Of String)
    End Class

    Async Function AddPCFunction() As Task
        Using httpClient As New HttpClient()
            PCName = AddPCI.Text
            Console.WriteLine(PCName)
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", PCClient.apiKey)
            httpClient.DefaultRequestHeaders.Add("PCName", PCName)

            Try
                If UseExistingC.Checked = True Then
                    'check if a value is in the list PCNames
                    If Not PCNames.Contains(ExistingPCName.Text) Then
                        MsgBox("PC does not exist")
                        Exit Function
                    End If

                    PCName = ExistingPCName.Text
                    My.Settings.PCName = PCName
                    My.Settings.Save()
                    PCClient.PCName = PCName
                    MsgBox("Success")
                    PCClient.AddPCItem.Enabled = False
                    PCClient.ContextMenu.Items.Remove(PCClient.AddPCItem)

                    PCClient.ControlPanelItem = PCClient.ContextMenu.Items.Add("Control Panel")
                    AddHandler PCClient.ControlPanelItem.Click, AddressOf PCClient.ControlPanelItem_Click
                    Me.Close()

                    PCClient.Running()
                Else

                    ' Await the result of PostAsync

                    If PCNames.Contains(PCName) Then
                        MsgBox("PC already exists")
                        Exit Function
                    End If

                    ' Await the result of PostAsync
                    Dim AddPCResponse As HttpResponseMessage = Await httpClient.GetAsync(addPCApiUrl)

                    ' Read the response content asynchronously
                    Dim AddPCResponseContent As String = Await AddPCResponse.Content.ReadAsStringAsync()

                    If AddPCResponseContent = "PC added successfully" & vbLf Then
                        MsgBox("Success")
                        My.Settings.PCName = PCName
                        My.Settings.Save()
                        PCClient.PCName = PCName
                        PCClient.AddPCItem.Enabled = False
                        PCClient.ContextMenu.Items.Remove(PCClient.AddPCItem)

                        PCClient.ControlPanelItem = PCClient.ContextMenu.Items.Add("Control Panel")
                        AddHandler PCClient.ControlPanelItem.Click, AddressOf PCClient.ControlPanelItem_Click
                        Me.Close()

                        PCClient.Running()
                    ElseIf AddPCResponseContent = "Please fill in all the fields" Then
                        MsgBox("Please fill in all the fields")

                    Else
                        MsgBox(AddPCResponseContent)
                    End If
                End If


            Catch ex As Exception
                MsgBox("Error: " & ex.Message)
            End Try

        End Using
    End Function

    Private Sub AddPCB_Click(sender As Object, e As EventArgs) Handles AddPCB.Click

        AddPCFunction()
    End Sub

    Private Async Sub AddPC_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ControlPanel.Close()
        Login.Close()
        PCClient.AddPCItem = PCClient.ContextMenu.Items.Add("Add PC")
        AddHandler PCClient.AddPCItem.Click, AddressOf PCClient.AddPCItem_Click

        Using httpClient As New HttpClient()
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", PCClient.apiKey)

            Dim GetPCNameResponse As HttpResponseMessage = Await httpClient.GetAsync(GetPCApiUrl)

            ' Read the response content asynchronously
            Dim GetPCNameResponseContent As String = Await GetPCNameResponse.Content.ReadAsStringAsync()

            Dim PCNamesResponse As PCNamesResponse = JsonConvert.DeserializeObject(Of PCNamesResponse)(GetPCNameResponseContent)
            PCNames = PCNamesResponse.PCNames

            If PCNames.Count < 1 Then
                UseExistingC.Checked = False
                UseExistingC.Enabled = False
                ExistingPCName.Enabled = False
                ExistingPCName.Visible = False
                AddPCI.Visible = True
                AddPCI.Enabled = True

            Else
                UseExistingC.Enabled = True
                ExistingPCName.Enabled = True
                ExistingPCName.Visible = True
                AddPCI.Visible = False
                AddPCI.Enabled = False
                For Each PCName As String In PCNames
                    ExistingPCName.Items.Add(PCName)
                Next

            End If
        End Using

    End Sub

    Private Sub UseExistingC_CheckedChanged(sender As Object, e As EventArgs) Handles UseExistingC.CheckedChanged
        If UseExistingC.Checked = True Then
            ExistingPCName.Enabled = True
            ExistingPCName.Visible = True
            AddPCI.Visible = False
            AddPCI.Enabled = False
        Else
            ExistingPCName.Enabled = False
            ExistingPCName.Visible = False
            AddPCI.Visible = True
            AddPCI.Enabled = True

        End If
    End Sub

End Class