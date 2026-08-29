Module Program

    <STAThread>
    Public Sub Main()
        ' Enable WinForms visuals
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' Run your tray application context or your main form here
        Application.Run(New PCClient())  ' Or a custom ApplicationContext
    End Sub

End Module
