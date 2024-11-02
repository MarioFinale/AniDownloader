Option Strict On
Option Explicit On
Imports System.IO

Namespace Utility
    Public Class SimpleLogger

#Region "Private vars"
        Private _userData As New List(Of String())
        Private LogQueue As New Queue(Of String())
        Private _endLog As Boolean = False
        Property LogPath As String
        Private _userPath As String
        Private _logging As Boolean
        Private _defaultUser As String
        Private _Debug As Boolean
        Private _maxLogLenght As Integer = 8000
        Private _verbose As Boolean = False
        Property Codename As String

#End Region

#Region "Properties"
        Public _logData As New List(Of String())
        ''' <summary>
        ''' Retorna una lista con todos los eventos en el LOG hasta el momento que se solicita.
        ''' </summary>
        ''' <returns></returns>
        Public ReadOnly Property Logdata() As List(Of String())
            Get
                Return _logData
            End Get
        End Property
        ''' <summary>
        ''' Indica si está en modo verboso.
        ''' </summary>
        ''' <returns></returns>
        Public ReadOnly Property Verbose() As Boolean
            Get
                Return _verbose
            End Get
        End Property
        ''' <summary>
        ''' Retorna una lista con los usuarios que tienen programados avisos de inactividad.
        ''' </summary>
        ''' <returns></returns>
        Public ReadOnly Property LogUserData As List(Of String())
            Get
                Return _userData
            End Get
        End Property
        ''' <summary>
        ''' Si se establece como true, finaliza toda actividad de log y los eventos siguientes no serán guardados en el archivo de LOG (Pero sí en memoria). Es recomendable usar el método .Dispose en su lugar.
        ''' </summary>
        ''' <returns></returns>
        Public Property EndLog As Boolean
            Get
                Return _endLog
            End Get
            Set(value As Boolean)
                _endLog = value
            End Set
        End Property

        Public Property Debug As Boolean
            Get
                Return _Debug
            End Get
            Set(value As Boolean)
                _Debug = value
            End Set
        End Property

#End Region

#Region "Create and Dispose"
        ''' <summary>
        ''' Crea una nueva instancia del motor de registros locales.
        ''' </summary>
        ''' <param name="LogPath">Archivo con ruta donde se guardará el archivo de LOG.</param>
        ''' <param name="UserPath">Archivo con ruta donde se guardará el archivo de usuarios.</param>
        Public Sub New(ByVal LogPath As String, ByVal UserPath As String, ByVal DefaultUser As String, ByVal VerboseMode As Boolean)
            _Debug = False
            _LogPath = LogPath
            _userPath = UserPath
            _defaultUser = DefaultUser
            _verbose = VerboseMode
            Task.Run(Sub()
                         If Not IO.File.Exists(LogPath) Then
                             IO.File.Create(LogPath).Close()
                         End If
                         Do Until _endLog
                             SaveLogWorker()
                             _logging = True
                             System.Threading.Thread.Sleep(1000)
                         Loop
                         _logging = False
                     End Sub)
            LoadUsers()
        End Sub
        ''' <summary>
        ''' Cierra los eventos de log correctamente.
        ''' </summary>
        Public Sub EndLogging()
            EndLog = True
            Do Until _logging = False
                System.Threading.Thread.Sleep(100)
            Loop
        End Sub

#End Region

#Region "Log Functions"
        ''' <summary>
        ''' Registra un evento normal.
        ''' </summary>
        ''' <param name="message">Texto del evento</param>
        ''' <param name="source">origen del evento</param>
        ''' <returns></returns>
        Function Log(ByVal message As String, source As String) As Boolean
            Return Log(message, source, Codename)
        End Function

        ''' <summary>
        ''' Registra un evento de tipo debug.
        ''' </summary>
        ''' <param name="message">Texto del evento</param>
        ''' <param name="source">origen del evento</param>
        ''' <returns></returns>
        Function Debug_Log(ByVal message As String, source As String) As Boolean
            Return Debug_Log(message, source, Codename)
        End Function

        ''' <summary>
        ''' Registra una excepción.
        ''' </summary>
        ''' <param name="message">Texto del evento</param>
        ''' <param name="source">origen del evento</param>
        ''' <returns></returns>
        Function EX_Log(ByVal message As String, source As String) As Boolean
            Return EX_Log(message, source, Codename)
        End Function

        ''' <summary>
        ''' Inicia otro thread para guardar un evento de log
        ''' </summary>
        ''' <param name="message">Texto a registrar</param>
        ''' <param name="source">Fuente del evento</param>
        ''' <param name="user">Usuario origen del evento</param>
        ''' <returns></returns>
        Public Function Log(ByVal message As String, ByVal source As String, ByVal user As String) As Boolean
            AddEvent(message, source, user, "LOG")
            WriteLine("LOG", source, user & ": " & message)
            Return True
        End Function

        ''' <summary>
        ''' Inicia otro thread para guardar un evento de log (debug)
        ''' </summary>
        ''' <param name="message">Texto a registrar</param>
        ''' <param name="source">Fuente del evento</param>
        ''' <param name="user">Usuario origen del evento</param>
        ''' <returns></returns>
        Public Function Debug_Log(ByVal message As String, ByVal source As String, ByVal user As String) As Boolean
            If _Debug Then
                AddEvent(message, source, user, "DEBUG")
            End If
            If _verbose Then WriteLine("DEBUG", source, user & ": " & message)
            Return True
        End Function

        ''' <summary>
        ''' Inicia otro thread para guardar un evento de log
        ''' </summary>
        ''' <param name="message">Texto a registrar</param>
        ''' <param name="source">Fuente del evento</param>
        ''' <param name="user">Usuario origen del evento</param>
        ''' <returns></returns>
        Public Function EX_Log(ByVal message As String, ByVal source As String, ByVal user As String) As Boolean
            AddEvent(message, source, user, "EX")
            WriteLine("EX", source, user & ": " & message)
            Return True
        End Function

        ''' <summary>
        ''' Guarda todos los usuarios y operadores en memoria al archivo.
        ''' </summary>
        ''' <returns></returns>
        Function SaveUsersToFile() As Boolean
            Dim StringToFile As New List(Of String)

            For Each Line As String() In _userData
                Dim Linetxt As String = String.Empty
                For Each item As String In Line
                    Linetxt = Linetxt & PsvSafeEncode(item) & "|"
                Next
                Linetxt = Linetxt.Trim(CType("|", Char))
                StringToFile.Add(Linetxt)
            Next

            Try
                IO.File.WriteAllLines(_userPath, StringToFile.ToArray)
                LoadUsers()
                Return True
            Catch ex As IO.IOException
                Debug_Log(Reflection.MethodBase.GetCurrentMethod().Name & " EX: " & ex.Message, "IRC", _defaultUser)
                'Do something, idk...\
                LoadUsers()
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Añade un nuevo usuario a la lista de aviso de inactividad de usuario
        ''' </summary>
        ''' <param name="UserAndTime">Array con {usuario a avisar, tiempo en formato d.hh:mm, operador} </param>
        ''' <returns></returns>
        Function SetUserTime(ByVal UserAndTime As String()) As Boolean
            If Not UserAndTime.Count < 3 Then Return False
            Try
                Dim RequestedUser As String = UserAndTime(1)
                Dim UserTime As String = UserAndTime(2)
                Dim OP As String = UserAndTime(0)
                Dim UserList As New List(Of Integer)
                For Each line As String() In _userData
                    If line(0) = RequestedUser Then
                        UserList.Add(_userData.IndexOf(line))
                    End If
                Next
                Dim IsInList As Boolean = False
                Dim IsInListIndex As Integer = -1
                If UserList.Count >= 1 Then
                    For Each i As Integer In UserList
                        If _userData(i)(1) = OP Then
                            IsInList = True
                            IsInListIndex = i
                        End If
                    Next
                Else
                End If
                If IsInList Then
                    _userData(IsInListIndex) = {OP, RequestedUser, UserTime}
                Else
                    _userData.Add({OP, RequestedUser, UserTime})
                End If
                SaveUsersToFile()
                Return True
            Catch ex As IndexOutOfRangeException
                Debug_Log(Reflection.MethodBase.GetCurrentMethod().Name & " EX: " & ex.Message, "IRC", _defaultUser)
                Return False
            End Try

        End Function

        ''' <summary>
        ''' Carga los usuarios desde el archivo de usuarios y los guarda en la variable local.
        ''' </summary>
        ''' <returns></returns>
        Function LoadUsers() As Boolean
            Try
                _userData = GetUsersFromFile()
                Return True
            Catch ex As IndexOutOfRangeException
                Debug_Log(Reflection.MethodBase.GetCurrentMethod().Name & " EX: " & ex.Message, "IRC", _defaultUser)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Escribe una línea en la salida del programa a modo de registro siguiendo un formato estándar 
        ''' (en realidad es completamente arbitrario pero está ordenado y bonito :) ).
        ''' </summary>
        ''' <param name="type">Tipo de registro</param>
        ''' <param name="source">Origen del registro</param>
        ''' <param name="message">Mensaje de salida</param>
        Public Shared Sub WriteLine(ByVal type As String, ByVal source As String, message As String)
            Dim msgstr As String = "[" & DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") & "]" & " [" & source & " " & type & "] " & message.Replace(Environment.NewLine, " "c)
            'Console.WriteLine(msgstr)
        End Sub

        ''' <summary>
        ''' Codifica texto para ser guardado en el LOG.
        ''' </summary>
        ''' <param name="text">Texto a codificar</param>
        ''' <returns></returns>
        Public Shared Function PsvSafeEncode(ByVal text As String) As String
            Return text.Replace("|"c, "%CHAR:U+007C%").Replace(vbCrLf, " "c).Replace(vbCr, " "c).Replace(vbLf, " "c).Replace(Environment.NewLine, " "c)
        End Function

        ''' <summary>
        ''' Decodifica texto guardado en el LOG.
        ''' </summary>
        ''' <param name="text">Texto a decodificar.</param>
        ''' <returns></returns>
        Public Shared Function PsvSafeDecode(ByVal text As String) As String
            Return text.Replace("%CHAR:U+007C%", "|")
        End Function

        Public Shared Function GetSubArray(ByVal str As String(), index As Integer) As String()
            If str.Count = 0 Then Return str
            Dim l As New List(Of String)
            For i As Integer = index To str.Count - 1
                l.Add(str(i))
            Next
            Return l.ToArray
        End Function
#End Region

#Region "Data Functions"
        ''' <summary>
        ''' Guarda los datos en el archivo de log, es llamado por otros threads.
        ''' </summary>
        Private Sub SaveLogWorker()
            SaveData(LogQueue)
            SyncLock (_logData)
                If _logData.Count > 100 Then
                    _logData.RemoveRange(0, 99)
                End If
            End SyncLock
        End Sub

        ''' <summary>
        ''' Guarda los datos desde un queue a un archivo de log.
        ''' </summary>
        ''' <param name="_queue"></param>
        ''' <returns></returns>
        Private Function SaveData(ByRef _queue As Queue(Of String())) As Boolean
            If String.IsNullOrWhiteSpace(_LogPath) Then Return False
            SyncLock _LogPath
                SyncLock _queue
                    Try
                        Do Until _queue.Count = 0
                            Dim tlines As String() = SafeDequeue(_queue)
                            Try
                                AppendLinesToText(_LogPath, tlines)
                                Threading.Thread.Sleep(20)
                            Catch ex As IOException
                                SafeEnqueue(_queue, tlines)
                                Threading.Thread.Sleep(50)
                            End Try
                        Loop
                        Dim totallines As String() = Array.Empty(Of String)
                        For i As Integer = 0 To 5
                            Try
                                totallines = IO.File.ReadAllLines(_LogPath)
                                Exit For
                            Catch ex As IOException
                                If i = 5 Then
                                    Throw
                                End If
                                Threading.Thread.Sleep(10)
                            End Try
                        Next
                        If totallines.Count > _maxLogLenght Then
                            Dim newarr As String() = GetSubArray(totallines, totallines.Count - _maxLogLenght)
                            IO.File.WriteAllLines(_LogPath, newarr)
                        End If
                        Return True
                    Catch f As FileNotFoundException
                        Return False
                    Catch ex As Exception
                        Debug_Log(ex.Message, Reflection.MethodBase.GetCurrentMethod().Name, _defaultUser)
                        Return False
                    End Try
                End SyncLock
            End SyncLock
        End Function

        ''' <summary>
        ''' Añade un evento al queue
        ''' </summary>
        ''' <param name="text">Texto a registrar</param>
        ''' <param name="Source">Fuente del evento</param>
        ''' <param name="User">Usuario origen del evento</param>
        ''' <param name="Type">Tipo de evento</param>
        ''' <returns></returns>
        Private Function AddEvent(ByVal text As String, Source As String, User As String, Type As String) As Boolean
            Dim CurrDate As String = Date.Now().ToString("dd/MM/yyyy HH:mm:ss")
            text = PsvSafeEncode(text)
            Source = PsvSafeEncode(Source)
            User = PsvSafeEncode(Type)
            SafeEnqueue(LogQueue, {CurrDate, text, Source, User, Type})
            Return True
        End Function

        ''' <summary>
        ''' Entrega el último registro de eventos.
        ''' </summary>
        ''' <param name="source">Fuente desde donde se solicita el último evento.</param>
        ''' <param name="user">Usuario que lo solicita.</param>
        ''' <returns></returns>
        Public Function Lastlog(ByRef source As String, user As String) As String()
            Dim logresponse As String() = Logdata.Last
            Log("Request of lastlog", source, user)
            Return logresponse
        End Function

        ''' <summary>
        ''' Añade líneas a un archivo de texto.
        ''' </summary>
        ''' <param name="FilePath">Ruta y nombre del archivo</param>
        ''' <param name="Lines">Líneas a añadir</param>
        ''' <returns></returns>
        Private Function AppendLinesToText(ByVal FilePath As String, Lines As String()) As Boolean
            If String.IsNullOrWhiteSpace(FilePath) Then Return False
            Try
                If Not IO.File.Exists(FilePath) Then
                    IO.File.Create(FilePath).Close()
                End If
                Dim Writer As New IO.StreamWriter(FilePath, True)
                SyncLock Writer
                    Dim LineStr As String = String.Empty
                    For Each item As String In Lines
                        LineStr = LineStr & item & "|"
                    Next
                    LineStr = LineStr.Trim(CType("|", Char))
                    Writer.WriteLine(LineStr)
                    Writer.Dispose()
                End SyncLock
                Return True
            Catch ex As IO.IOException
                Debug_Log(Reflection.MethodBase.GetCurrentMethod().Name & " EX: " & ex.Message, "Logger", _defaultUser)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Obtiene los usuarios desde el el archivo y los regresa como string()
        ''' </summary>
        ''' <returns></returns>
        Private Function GetUsersFromFile() As List(Of String())
            Dim UserList As New List(Of String())
            If Not IO.File.Exists(_userPath) Then
                IO.File.Create(_userPath).Close()
                Return UserList
            Else
                For Each line As String In IO.File.ReadAllLines(_userPath)
                    Dim Encodedline As New List(Of String)
                    For Each s As String In line.Split(CType("|", Char))
                        Encodedline.Add(PsvSafeDecode(s))
                    Next
                    UserList.Add(Encodedline.ToArray)
                Next
                Return UserList
            End If
        End Function

        ''' <summary>
        ''' Añade un item al queue de forma segura para ser llamado desde múltiples threads.
        ''' </summary>
        ''' <param name="_QueueToEnqueue">Queue a modificar</param>
        ''' <param name="str">Cadea de texto a añadir</param>
        Private Sub SafeEnqueue(ByVal _QueueToEnqueue As Queue(Of String()), ByVal str As String())
            SyncLock (_logData)
                _logData.Add(str)
            End SyncLock
            SyncLock (_QueueToEnqueue)
                _QueueToEnqueue.Enqueue(str)
            End SyncLock
        End Sub

        ''' <summary>
        ''' Saca un ítem de un queue de forma segura para ser llamado desde múltiples threads.
        ''' </summary>
        ''' <param name="QueueToDequeue"></param>
        ''' <returns></returns>
        Private Shared Function SafeDequeue(ByVal QueueToDequeue As Queue(Of String())) As String()
            SyncLock (QueueToDequeue)
                Return QueueToDequeue.Dequeue()
            End SyncLock
        End Function
#End Region

    End Class
End Namespace