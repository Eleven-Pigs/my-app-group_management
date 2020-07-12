﻿Imports ActiveDs
Imports System.DirectoryServices
Imports DsEntry = System.DirectoryServices.DirectoryEntry
''' <summary>
''' Represents an object for interfering with users and groups using the WinNT provider.
''' </summary>
''' <remarks></remarks>
Public Class ActiveDirectory
    Private main As DirectoryEntry
    Private mainF As MainForm
    Private isLoading_ As Boolean = True
    Private conErr As Boolean
    Private loadingCancelled As Boolean
    Private NoUserGroup As Boolean
    Private displayName As String
    Private sysSID, rID As String

    ''' <summary>
    ''' A list of the users on the machine, the username (map key) maps to its full name property (map value) to shorten search time.
    ''' </summary>
    ''' <remarks></remarks>
    Public ReadOnly UserList As New SortedDictionary(Of String, String)
    Public ReadOnly GroupList As New SortedSet(Of String)
    Public ReadOnly BuiltInPrincipals As List(Of BuiltInPrincipal)
    Public UserGroup As DsEntry

    ''' <summary>
    ''' Refreshes the list on the MainForm if the currently selected AD corresponds to this instance and reinits search if visible.
    ''' </summary>
    ''' <remarks></remarks>
    Private Sub UpdateLists()
        If Not mainF.ViewHandler.GetView() = ViewHandler_C.View.MachineRoot AndAlso mainF.ADHandler.currentAD().Equals(Me) Then
            mainF.ViewHandler.RefreshMainList()
        End If
        mainF.ViewHandler.RefreshSearch()
    End Sub

    ''' <summary>
    ''' Closes the DirectoryEntry object.
    ''' </summary>
    ''' <remarks></remarks>
    Public Sub Disconnect()
        RaiseEvent OnDisconnect()

        If isLoading_ Then
            loadingCancelled = True
        Else
            If main.Name <> "localhost" Then
                WNetClose(main.Name, mainF.Handle)
            End If
        End If

        main.Close()
    End Sub

    Public Shadows Function Equals(obj As ActiveDirectory) As Boolean
        If GetID() = obj.GetID() Then
            Return True
        Else
            Return False
        End If
    End Function

#Region "Constructors"

    Public Sub New(mainForm As MainForm)
        Me.New("localhost", mainForm, Environment.MachineName)
    End Sub

    Public Sub New(addr As String, mainForm As MainForm, Optional name As String = "")
        mainF = mainForm

        If name = "" Then
            displayName = addr
        Else
            displayName = name
        End If

        main = New DirectoryEntry("WinNT://" & addr)

        Dim PolicyHandle As IntPtr = GetPolicyHandle(addr, mainForm.Handle)
        If PolicyHandle <> IntPtr.Zero Then
            BuiltInPrincipals = LookupWellKnownSids(PolicyHandle, mainForm.Handle, [Enum].GetValues(GetType(WellKnownSID)))
            ClosePolicyHandle(PolicyHandle, mainF.Handle)
        End If
        
        init()
    End Sub

    Private Async Sub init()
        Try
            Await Task.Run(Sub()
                               main.Children.SchemaFilter.Add("User")
                               main.Children.SchemaFilter.Add("Group")
                               RefreshDS()
                           End Sub)
        Catch ex As Exception
            If loadingCancelled = False Then
                'Set the connection error flag
                conErr = True
            End If
        End Try
    End Sub
#End Region

#Region "Events"

    Public Event OnDisconnect()
    Public Event OnDisplayNameChange(newName As String)
    Public Event OnDeleteUser(Name As String)
    Public Event OnDeleteGroup(Name As String)
    Public Event OnRenameUser(Name As String, newName As String)
    Public Event OnRenameGroup(Name As String, newName As String)
#End Region

#Region "Getters and setters"

    Public Function IsLoading() As Boolean
        Return isLoading_
    End Function

    Public Function ConnectionErrorOccurred() As Boolean
        Return conErr
    End Function

    Public Function GetDisplayName() As String
        Return displayName
    End Function

    Public Function GetName() As String
        Return main.Name
    End Function

    Public Function UserGroupUnavailable() As Boolean
        Return NoUserGroup
    End Function

    ''' <summary>
    ''' Retrieves an unique identifier for this particular AD object.
    ''' </summary>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Function GetID() As String
        If sysSID IsNot Nothing AndAlso rID IsNot Nothing Then
            Return sysSID & rID
        Else
            sysSID = "$RND" & (New Random).Next().ToString()
            rID = "$RND" & (New Random).Next().ToString()
            Return sysSID & rID
        End If
    End Function

    ''' <summary>
    ''' Returns the machine's SID.
    ''' </summary>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Function GetSystemSID() As String
        If sysSID IsNot Nothing Then
            Return sysSID
        Else
            sysSID = "$RND" & (New Random).Next().ToString()
            Return sysSID
        End If
    End Function

    Public Sub ChangeDisplayName(newName As String)
        If newName = "" Then
            displayName = main.Name
        Else
            displayName = newName
        End If
        RaiseEvent OnDisplayNameChange(newName)
    End Sub
#End Region

#Region "Methods for locating objects"

    ''' <summary>
    ''' Returns whether a user exists. If the user cannot be found, an error message is displayed to the user and the method returns false.
    ''' </summary>
    ''' <param name="Name"></param>
    ''' <param name="parentWnd"></param>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Function UserExists(Name As String, parentWnd As IntPtr) As Boolean
        Return FindUser(Name, parentWnd) IsNot Nothing
    End Function

    ''' <summary>
    ''' Returns whether a group exists. If the group cannot be found, an error message is displayed to the user and the method returns false.
    ''' </summary>
    ''' <param name="Name"></param>
    ''' <param name="parentWnd"></param>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Function GroupExists(Name As String, parentWnd As IntPtr) As Boolean
        Return FindGroup(Name, parentWnd) IsNot Nothing
    End Function

    Public Function BuiltInPrincipalOrUserExists(Name As String, parentWnd As IntPtr) As Boolean
        If GetPrincipalByName(Name).isInvalid = False Then
            Return True
        End If

        Return UserExists(Name, parentWnd)
    End Function

    Public Function GetPrincipalBySID(SID As String) As BuiltInPrincipal
        For i As Integer = 0 To BuiltInPrincipals.Count - 1
            If BuiltInPrincipals(i).SID = SID Then
                Return BuiltInPrincipals(i)
            End If
        Next

        Return BuiltInPrincipal.INVALID
    End Function

    Public Function GetPrincipalByName(Name As String) As BuiltInPrincipal
        For i As Integer = 0 To BuiltInPrincipals.Count - 1
            If BuiltInPrincipals(i).Name = Name Then
                Return BuiltInPrincipals(i)
            End If
        Next

        Return BuiltInPrincipal.INVALID
    End Function

    ''' <summary>
    ''' Finds a user in the account database. If the object cannot be found, an error message is displayed to the user and the method returns null.
    ''' </summary>
    ''' <param name="Name"></param>
    ''' <param name="parentWnd"></param>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Function FindUser(Name As String, parentWnd As IntPtr) As DsEntry
        Try
            Return main.Children.Find(Name, "User")
        Catch ex As Runtime.InteropServices.COMException
            If ShowCOMErr(ex.ErrorCode, parentWnd, ex.Message, Name) = COMErrResult.REFRESH Then
                RefreshDS()
                mainF.ViewHandler.RefreshItemCount()
            End If
            Return Nothing
        Catch ex As Exception
            ShowUnknownErr(parentWnd, ex.Message)
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' Finds a group in the account database. If the object cannot be found, an error message is displayed to the user and the method returns null.
    ''' </summary>
    ''' <param name="Name"></param>
    ''' <param name="parentWnd"></param>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Public Function FindGroup(Name As String, parentWnd As IntPtr) As DsEntry
        Try
            Return main.Children.Find(Name, "Group")
        Catch ex As Runtime.InteropServices.COMException
            If ShowCOMErr(ex.ErrorCode, parentWnd, ex.Message, Name) = COMErrResult.REFRESH Then
                RefreshDS()
                mainF.ViewHandler.RefreshItemCount()
            End If
            Return Nothing
        Catch ex As Exception
            ShowUnknownErr(parentWnd, ex.Message)
            Return Nothing
        End Try
    End Function

    Public Function GetPath(Name As String, IncludeBuiltInSecurityPrincipals As Boolean) As String

        If IncludeBuiltInSecurityPrincipals Then
            Dim Principal As BuiltInPrincipal = GetPrincipalByName(Name)

            If Principal.isInvalid = False Then
                Return "WinNT://" & Principal.SID
            End If
        End If

        Return "WinNT://" & Name
    End Function
#End Region

    ''' <summary>
    ''' Refreshes the user and group database.
    ''' </summary>
    ''' <remarks></remarks>
    Public Sub RefreshDS()
        GroupList.Clear()
        UserList.Clear()

        Dim userGroupFound As Boolean = False

        For Each o As DsEntry In main.Children
            If isLoading_ AndAlso loadingCancelled Then
                Return
            End If

            If o.SchemaClassName = "Group" Then
                GroupList.Add(o.Name)
                'Find the user group using its SID as it should be the default for new user accounts
                If Not userGroupFound AndAlso PtrToSID(o.Properties("objectSid").Value) = "S-1-5-32-545" Then
                    UserGroup = o
                    userGroupFound = True
                End If

            ElseIf o.SchemaClassName = "User" Then
                UserList.Add(o.Name, o.Properties("FullName").Value.ToString())

                'Get the system SID by parsing it from the built-in Administrator account
                If sysSID Is Nothing Then
                    Dim SID As String = PtrToSID(o.Properties("objectSid").Value)
                    If SID.EndsWith("-500") AndAlso SID.StartsWith("S-1-5-21-") Then
                        sysSID = SID.Substring(0, SID.Length - 4)
                    End If
                End If

            End If
        Next

        If isLoading_ AndAlso loadingCancelled Then
            Return
        End If

        If UserGroup Is Nothing Then
            NoUserGroup = True
        End If

        'This code is only executed the first time refreshed (i.e. on init only)
        If isLoading_ Then
            If sysSID Is Nothing Then
                sysSID = "$RND" & (New Random).Next().ToString()
            End If
            rID = "$RND" & (New Random).Next().ToString()
            isLoading_ = False
            Return
        End If

        UpdateLists()
    End Sub

#Region "Rename handlers"

    ''' <summary>
    ''' Trigger a full name change in order to update the main list.
    ''' </summary>
    ''' <param name="User"></param>
    ''' <param name="newFullName"></param>
    ''' <remarks></remarks>
    Public Sub UserFullNameChanged(User As String, newFullName As String)
        UserList(User) = newFullName
        UpdateLists()
    End Sub

    Public Function RenameUser(Name As String, newName As String, parentWnd As IntPtr) As Boolean
        Dim dsuserp As DsEntry = FindUser(Name, parentWnd)
        If dsuserp IsNot Nothing AndAlso newName <> dsuserp.Name Then
            Try
                dsuserp.Rename(newName)
                dsuserp.CommitChanges()

                Dim fullName As String = UserList(Name)
                UserList.Remove(Name)
                UserList.Add(newName, fullName)
                UpdateLists()

                RaiseEvent OnRenameUser(Name, newName)
                Return True
            Catch ex As UnauthorizedAccessException
                ShowPermissionDeniedErr(mainF.Handle)
                Return False
            Catch ex As Runtime.InteropServices.COMException
                If ex.ErrorCode = COMErrorCodes.GROUP_NOT_FOUND_USER Then
                    'Special case for this error; the error code does not match GROUP_ALREADY_EXISTS.
                    TaskDialog(parentWnd, "Local users and groups", "Group already exists", "There is already a group associated with that name. Please choose a different name.", TASKDIALOG_COMMON_BUTTON_FLAGS.TDCBF_OK_BUTTON, TASKDIALOG_ICONS.TD_ERROR_ICON, 0)
                    Return False
                End If
                If ShowCOMErr(ex.ErrorCode, parentWnd, ex.Message, Name) = COMErrResult.REFRESH Then
                    RefreshDS()
                End If
                Return False
            Catch ex As Exception
                ShowUnknownErr(parentWnd, ex.Message, "Issue occurred at: RenameUser")
                Return False
            End Try
        End If
        Return False
    End Function

    Public Function RenameGroup(Name As String, newName As String, parentWnd As IntPtr) As Boolean
        Dim dsgrp As DsEntry = FindGroup(Name, parentWnd)
        If dsgrp IsNot Nothing AndAlso newName <> dsgrp.Name Then
            Try
                dsgrp.Rename(newName)
                dsgrp.CommitChanges()

                GroupList.Remove(Name)
                GroupList.Add(newName)
                UpdateLists()

                RaiseEvent OnRenameGroup(Name, newName)
                Return True
            Catch ex As UnauthorizedAccessException
                ShowPermissionDeniedErr(parentWnd)
                Return False
            Catch ex As Runtime.InteropServices.COMException
                If ShowCOMErr(ex.ErrorCode, parentWnd, ex.Message, Name) = COMErrResult.REFRESH Then
                    RefreshDS()
                End If
                Return False
            Catch ex As Exception
                ShowUnknownErr(parentWnd, ex.Message, "Issue occurred at: RenameGroup")
                Return False
            End Try
        End If
        Return False
    End Function
#End Region

#Region "Methods for creating and deleting objects"

    Public Function CreateUser(Username As String, Fullname As String) As DsEntry
        Dim newUser As DsEntry = main.Children.Add(Username, "User")
        newUser.Properties("FullName").Value = Fullname
        newUser.CommitChanges()

        UserList.Add(Username, Fullname)
        UpdateLists()
        Return newUser
    End Function

    Public Function CreateGroup(Name As String, Comment As String) As DsEntry
        Dim newGroup As DsEntry = main.Children.Add(Name, "Group")
        newGroup.Properties("Description").Value = Comment
        newGroup.CommitChanges()

        GroupList.Add(Name)
        UpdateLists()
        Return newGroup
    End Function

    Public Sub DeleteUser(Name As String, parentWnd As IntPtr, Optional pUpdateLists As Boolean = True)
        Dim user As DsEntry = FindUser(Name, parentWnd)
        If user IsNot Nothing Then
            main.Children.Remove(user)
            UserList.Remove(Name)
            If pUpdateLists Then UpdateLists()
            RaiseEvent OnDeleteUser(Name)
        End If
    End Sub

    Public Sub DeleteGroup(Name As String, parentWnd As IntPtr, Optional pUpdateLists As Boolean = True)
        Dim group As DsEntry = FindGroup(Name, parentWnd)
        If group IsNot Nothing Then
            main.Children.Remove(group)
            GroupList.Remove(Name)
            If pUpdateLists Then UpdateLists()
            RaiseEvent OnDeleteGroup(Name)
        End If
    End Sub
#End Region
End Class