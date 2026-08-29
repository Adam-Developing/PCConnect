Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading.Tasks
Imports System.Web.Script.Serialization



Public Class Login

    Dim jss As New JavaScriptSerializer()
    Dim Time As String
    Dim apiKey As String
    'Dim Operations As List(Of String) = New List(Of String) From {"Shutdown", "Restart", "Logoff", "Lock", "Sleep", "Hibernate"}
    Dim loginApiUrl As String = "https://pcconnect.adamkhattab.co.uk/api/login.php" ' Replace with the actual URL of your PHP script
    Dim Username As String
    Dim Password As String
    Dim output As String
    Dim hashedPassword
    Function HashPassword(password As String) As String
        ' Convert the password string to bytes
        Dim passwordBytes As Byte() = Encoding.UTF8.GetBytes(password)

        ' Create an instance of the SHA-256 hash algorithm
        Using sha256 As SHA256 = SHA256.Create()
            ' Compute the hash from the password bytes
            Dim hashedBytes As Byte() = sha256.ComputeHash(passwordBytes)

            ' Convert the hashed bytes to a hexadecimal string
            Dim hashedString As String = BitConverter.ToString(hashedBytes).Replace("-", "").ToLower()

            Return hashedString
        End Using
    End Function

    Function LoginFunction(Username, Password)
        Using httpClient As New HttpClient()
            Try

                ' Define the data to send in the POST request
                Dim loginPostData As New List(Of KeyValuePair(Of String, String))
                loginPostData.Add(New KeyValuePair(Of String, String)("loginUsername", Username))
                loginPostData.Add(New KeyValuePair(Of String, String)("loginPassword", Password))

                ' Create the HttpContent object with the content you want to send
                Dim content As New FormUrlEncodedContent(loginPostData)

                ' Send the POST request
                Dim LoginResponse As HttpResponseMessage = httpClient.PostAsync(loginApiUrl, content).Result

                ' Check if the request was successful (HTTP status code 200-299)
                If LoginResponse.IsSuccessStatusCode Then
                    ' Read and process the response content
                    Dim LoginResponseContent As String = LoginResponse.Content.ReadAsStringAsync().Result

                    If LoginResponseContent <> "Invalid username or password." Then

                        Return LoginResponseContent
                    Else
                        Return "Invalid username or password."
                    End If
                Else
                    Return "Error: " & LoginResponse.StatusCode.ToString() & " - " & LoginResponse.ReasonPhrase
                End If

            Catch ae As AggregateException
                For Each innerExeption As Exception In ae.InnerExceptions
                    If TypeOf innerExeption Is HttpRequestException Then
                        ControlPanel.internetChange(0)
                        MsgBox("No internet connection")
                    End If
                Next

            Catch ex As WebException

                ControlPanel.internetChange(0)
                MsgBox("Please try again later, our systems are under maintinace")

            Catch ex As HttpRequestException

                ControlPanel.internetChange(0)
                MsgBox("No internet connection")
            End Try


        End Using
    End Function

    Private Sub SubmitB_Click(sender As Object, e As EventArgs) Handles SubmitB.Click

        ' Create an instance of HttpClient
        Username = UsernameI.Text
        Password = PasswordI.Text
        hashedPassword = HashPassword(Password)

        output = LoginFunction(Username, hashedPassword)
        If output <> "Invalid username or password." Then

            My.Settings.Username = Username
            My.Settings.Password = hashedPassword
            My.Settings.Save()
            Me.Close()
            PCClient.Show()
            PCClient.Usercontrol.Text = "Logout"
            'apiKey = output
            MsgBox("Login Successful.")
            PCClient.apiKey = output

            AddPC.Show()
            PCClient.Running()
            'Application.Restart()
        ElseIf output = "Invalid username or password." Then
            MsgBox("Invalid username or password.")
        ElseIf output.Contains("Error") Then
            MsgBox(output)

        End If

    End Sub

End Class
