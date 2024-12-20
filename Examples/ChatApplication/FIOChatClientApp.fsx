(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

open System
open System.Net.Sockets
open System.Globalization

#load "..\..\FIO\FIO.Core.DSL.fs"
open FIO.Core.DSL

#load "..\..\FIO\FIO.Core.CE.fs"
open FIO.Core.CE

#load "..\..\FIO\FIO.Runtime.Core.fs"
open FIO.Runtime

#load "..\..\FIO\FIO.Runtime.Advanced.fs"
open FIO.Runtime.Advanced

#load "..\..\FIO\FIO.Core.App.fs"
open FIO.Core.App

#r "nuget: FSharp.SystemTextJson, 1.3.13"
#load "..\..\FIO\FIO.Lib.Network.fs"
open FIO.Lib.Network.Socket

type Message =
    // A client sends a ConnectionRequest to the server to be connected to the chat with the given Username.
    | ConnectionRequest of Username: string
    // A server sends a ConnectionAcceptedResponse to a client that has successfully connected as a response to ConnectionRequest with the AcceptUsername.
    | ConnectionAcceptedResponse of ServerName: string * AcceptedUsername: string * Content: string
    // A server sends a ConnectionFailedResponse to a client that has failed to connect as a response to ConnectionRequest.
    | ConnectionFailedResponse of ServerName: string * FailedUsername: string * Content: string
    // A client sends a BroadcastMessageRequest to the server to be broadcasted to all connected clients.
    | BroadcastMessageRequest of SenderUsername: string * Content: string
    // A server sends a BroadcastMessageResponse to all connected clients as a response to BroadcastMessageRequest.
    | BroadcastMessageResponse of SenderUsername: string * Content: string
    // A client sends a PrivateMessageRequest to the server to be sent to a specific client.
    | PrivateMessageRequest of SenderUsername: string * ReceiverUsername: string * Content: string 
    // A server sends a PrivateMessageResponse to the receiver of a private message request.
    | PrivateMessageResponse of SenderUsername: string * Content: string
    // A server sends a PrivateMessageFailedResponse to the sender of a private message request if it fails.
    | PrivateMessageFailedResponse of ServerName: string * SenderUsername: string * ReceiverUsername: string * Content: string
    | OnlineClientListRequest of SenderUsername: string
    | OnlineClientListResponse of SenderUsername: string * ReceiverUsername: string * ClientList: string list

type FIOChatClientApp(serverIp: string, port: int, username: string) =
    inherit FIOApp<unit, exn>()

    let clientUsername = "FIOChatClient"
    let getFormattedDate() = DateTime.Now.ToString("dd.MM.yy HH:mm:ss", CultureInfo.InvariantCulture)

    let getFormattedMessage(username, message) =
        $"[%s{getFormattedDate()}] [%s{username}]: %s{message}"

    let printMessage (username, message) =
        printfn $"%s{getFormattedMessage(username, message)}"

    let clearPrompt promptLength =
        Console.Write("\r" + new string(' ', promptLength) + "\r")

    let printPrompt () =
        printf($"""%s{getFormattedMessage(username, "")}""")

    let runClient username (serverIp: string) (port: int) =
        let sendMessages senderUsername (channel: SocketChannel<Message>) =
            fio {
                while true do
                    do! !+ printPrompt()
                    let! content = !+ Console.ReadLine()

                    if content.Trim().Length = 0 then
                        return ()
                    else
                        if content.Trim().StartsWith("@") then
                            let parts = content.Trim().Split(":")
                            let receiverName = parts.[0].Substring(1).Trim()
                            let message = parts.[1].Trim()
                            do! channel.Send(PrivateMessageRequest(senderUsername, receiverName, message))
                        else if content.Trim().StartsWith("\list") then
                            do! channel.Send(OnlineClientListRequest(senderUsername))
                        else
                            do! channel.Send(BroadcastMessageRequest(senderUsername, content))
            }

        let handleConnectionAccepted serverName acceptedUsername content =
            fio {
                match acceptedUsername = username with
                | true -> 
                    do! !+ printMessage(serverName, content)
                | _ ->
                    do! !+ printMessage(serverName, $"Received ConnectionAcceptedResponse for %s{acceptedUsername} with content: %s{content}.")
            }

        let handleConnectionFailed serverName failedUsername content : FIO<unit, exn> =
            fio {
                match failedUsername = username with
                | true -> 
                    do! !+ printMessage(serverName, content)
                | _ ->
                    do! !+ printMessage(serverName, $"Received ConnectionFailedResponse for %s{failedUsername}.")
                return! !- (new Exception(content)) // TODO: This is not working, this one?
            }

        let receiveMessages (channel: SocketChannel<Message>) =
            fio {
                while true do
                    let! message = channel.Receive()
                    clearPrompt 100
                    match message with
                    | ConnectionRequest requestUsername ->
                        do! !+ printMessage(clientUsername, $"Received ConnectionRequest from %s{requestUsername}.")
                    | ConnectionAcceptedResponse(serverName, acceptedUsername, content) ->
                        do! handleConnectionAccepted serverName acceptedUsername content
                    | ConnectionFailedResponse(serverName, failedUsername, content) -> 
                        do! handleConnectionFailed serverName failedUsername content
                    | BroadcastMessageRequest(senderUsername, content) ->
                        do! !+ printMessage(clientUsername, $"Received BroadcastMessageRequest for %s{senderUsername}. Content: %s{content}.")
                    | BroadcastMessageResponse(senderUsername, content) ->
                        do! !+ printMessage(senderUsername, content)
                    | PrivateMessageRequest (senderUsername, receiverUsername, content) -> 
                        do! !+ printMessage(clientUsername, $"Received PrivateMessageRequest from %s{senderUsername} to %s{receiverUsername} with content %s{content}.")
                    | PrivateMessageResponse(senderUsername, content) ->
                        do! !+ printMessage($"%s{senderUsername} -> you", content)
                    | PrivateMessageFailedResponse(serverName, _, _, content) ->
                        do! !+ printMessage(serverName, content)
                    | OnlineClientListRequest(senderUsername) ->
                        do! !+ printMessage(clientUsername, $"Received OnlineClientListRequest from %s{senderUsername}.")
                    | OnlineClientListResponse(senderUsername, _, clientList) ->
                        do! !+ printMessage(senderUsername, $"""Online clients: {String.Join(", ", clientList)}.""")
                    do! !+ printPrompt()
            }

        fio {
            let! clientSocket = !+ (new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            do! !+ clientSocket.Connect(serverIp, port)
            let! clientChannel = !+ SocketChannel<Message>(clientSocket)
            do! clientChannel.Send(ConnectionRequest username)
            return! sendMessages username clientChannel <!> receiveMessages clientChannel
        }

    override this.effect =
        runClient username serverIp port

let args = fsi.CommandLineArgs
if args.Length = 0 then
    printfn "No arguments were provided. Please provide server ip, port and username."
    exit 1
match Int32.TryParse(args.[2]) with
| true, port when port > 0 && port < 65536 -> 
    let serverIp = args.[1]
    let username = args.[3]
    FIOApp.Run(FIOChatClientApp(serverIp, port, username))
    exit 0
| _ ->
    printfn $"Invalid port number: %s{args.[2]}."
    exit 2