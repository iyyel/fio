(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

open System
open System.Net
open System.Net.Sockets
open System.Collections.Generic
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

and Server =
    { Name: string
      EndPoint: string
      Listener: TcpListener }

and FIOChatServerApp(serverIp, port) =
    inherit FIOApp<unit, exn>()

    let clients = Dictionary<string, SocketChannel<Message>>()

    let getFormattedDate() = 
        DateTime.Now.ToString("dd.MM.yy HH:mm:ss", CultureInfo.InvariantCulture)

    let printMessage(username, endpoint, message) =
        printfn $"[%s{getFormattedDate()}] [%s{username} / %s{endpoint}]: %s{message}"

    let runServer ip port =
        let broadcastMessage message senderName =
            match senderName with
            | Some senderName -> 
                clients
                |> Seq.filter (fun pair -> pair.Key <> senderName)
                |> Seq.map _.Value.Send(message)
                |> Seq.fold (fun acc effect -> acc <!> effect) (! ())
            | None ->
                clients
                |> Seq.map _.Value.Send(message)
                |> Seq.fold (fun acc effect -> acc <!> effect) (! ())

        let handleClient (clientChannel: SocketChannel<Message>) server =
            let handleConnectionRequest username =
                fio {
                    let! clientEndpoint = clientChannel.RemoteEndPoint()
                    do! !+ printMessage(server.Name, server.EndPoint, ($"Received connection request for %s{username} from %s{clientEndpoint.ToString()}."))
                    match clients.TryGetValue username with
                    | true, _ ->
                        let! content = !+ $"%s{username} is already connected. Connection failed!"
                        do! !+ printMessage(server.Name, server.EndPoint, content)
                        return! clientChannel.Send(ConnectionFailedResponse (server.Name, username, content))
                    | false, _ ->
                        let! serverContent = !+ $"%s{username} has connected from %s{clientEndpoint.ToString()}."
                        let! clientContent = !+ $"%s{username} has joined the chat. Welcome to %s{server.Name}!"
                        do! !+ clients.Add(username, clientChannel)
                        do! clientChannel.Send(ConnectionAcceptedResponse (server.Name, username, clientContent))
                        do! !+ printMessage(server.Name, server.EndPoint, serverContent)
                        return! broadcastMessage (BroadcastMessageResponse (server.Name, clientContent)) (Some username)
                }

            let handlePrivateMessageRequest senderUsername receiverUsername content clientEndPoint = 
                fio {
                    match clients.TryGetValue receiverUsername with
                    | true, receiverChannel ->
                        let! receiverEndpoint = receiverChannel.RemoteEndPoint()
                        do! !+ printMessage($"%s{senderUsername} -> %s{receiverUsername}", $"%s{clientEndPoint} -> %s{receiverEndpoint.ToString()}", content)
                        do! receiverChannel.Send(PrivateMessageResponse (senderUsername, content))
                    | false, _ ->
                        match clients.TryGetValue senderUsername with
                        | true, channel ->
                            let! serverContent = !+ $"%s{senderUsername} failed to private message %s{receiverUsername} because %s{receiverUsername} is not connected."
                            let! clientContent = !+ $"%s{receiverUsername} is not connected."
                            do! !+ printMessage(server.Name, server.EndPoint, serverContent)
                            do! channel.Send(PrivateMessageFailedResponse (server.Name, senderUsername, receiverUsername, clientContent))
                        | false, _ ->
                            do! !+ printMessage(server.Name, server.EndPoint, $"Failed handling private message request from %s{senderUsername} to %s{receiverUsername}. Neither the sender or receiver is connected.")
                }

            let handleMessage message clientEndPoint =
                fio {
                    match message with
                    | ConnectionRequest username ->
                        do! handleConnectionRequest username
                    | ConnectionAcceptedResponse(serverName, username, content) ->
                        do! !+ printMessage(server.Name, server.EndPoint, $"Received a ConnectionAcceptedResponse from %s{serverName} for %s{username} with content: %s{content}")
                    | ConnectionFailedResponse(serverName, username, content) ->
                         do! !+ printMessage(server.Name, server.EndPoint, $"Received a ConnectionFailedResponse from %s{serverName} for %s{username} with content: %s{content}")
                    | BroadcastMessageRequest(senderUsername, content) ->
                        do! !+ printMessage(senderUsername, clientEndPoint, content) 
                        do! broadcastMessage ((BroadcastMessageResponse (senderUsername, content))) (Some senderUsername)
                    | BroadcastMessageResponse(senderName, content) ->
                        do! !+ printMessage(server.Name, server.EndPoint, $"Received a BroadcastMessageResponse from %s{senderName} with content: %s{content}")
                    | PrivateMessageRequest(senderUsername, receiverUsername, content) ->
                        do! handlePrivateMessageRequest senderUsername receiverUsername content clientEndPoint
                    | PrivateMessageResponse (senderUsername, content) ->
                        do! !+ printMessage(server.Name, server.EndPoint, $"Received a PrivateMessageResponse from %s{senderUsername} with content: %s{content}")
                    | PrivateMessageFailedResponse (serverName, senderUsername, receiverUsername, content) ->
                        do! !+ printMessage(server.Name, server.EndPoint, $"Received a PrivateMessageFailedResponse from %s{serverName} for %s{senderUsername} to %s{receiverUsername} with content: %s{content}")
                    | OnlineClientListRequest senderUsername ->
                        let clientList = clients.Keys |> List.ofSeq
                        do! !+ printMessage(server.Name, server.EndPoint, $"Sent client list to %s{senderUsername}.")
                        do! !+ printMessage(server.Name, server.EndPoint, $"""Currently connected clients: %s{String.Join(", ", clientList)}.""")
                        do! clientChannel.Send(OnlineClientListResponse (server.Name, senderUsername, clientList))
                    | OnlineClientListResponse (serverName, senderUsername, clientList) ->
                        do! !+ printMessage(server.Name, server.EndPoint, $"""Received a OnlineClientListResponse from %s{serverName} for %s{senderUsername} with content: %s{String.Join(", ", clientList)}""")
                }

            fio {
                let! clientEndPoint = clientChannel.RemoteEndPoint()
                try
                    while true do
                        let! message = clientChannel.Receive()
                        do! handleMessage message (clientEndPoint.ToString())
                with exn ->
                    let clientUsername = 
                        match clients |> Seq.tryFind (fun pair -> pair.Value = clientChannel) with
                        | Some pair -> pair.Key
                        | _ -> "Unknown"
                    let! serverContent = !+ $"%s{clientUsername} from %s{clientEndPoint.ToString()} has disconnected. Error: %s{exn.Message}"
                    let! clientContent = !+ $"%s{clientUsername} has disconnected."
                    do! !+ (clients.Remove clientUsername |> ignore)
                    do! !+ printMessage(server.Name, server.EndPoint, serverContent)
                    do! broadcastMessage ((BroadcastMessageResponse (server.Name, clientContent))) None
                    do! clientChannel.Close()
            }

        let handleClients server =
            fio {
                while true do
                    let! clientSocket = !+ server.Listener.AcceptSocket()
                    let! clientChannel = !+ SocketChannel<Message>(clientSocket)
                    do! !!! (handleClient clientChannel server)
            }

        fio {
            let! listener = !+ (new TcpListener(ip, port))
            let! server = !+ { Name = "FIOChat"; EndPoint = listener.LocalEndpoint.ToString(); Listener = listener }
            do! !+ server.Listener.Start()
            do! !+ printMessage(server.Name, server.EndPoint, $"Server started on %s{server.EndPoint}. Listening for clients...")
            return! handleClients server
        }

    override this.effect =
        runServer serverIp port

let args = fsi.CommandLineArgs
if args.Length = 0 then
    eprintfn "No arguments were provided. Please provide port number."
    exit 1
match Int32.TryParse(args.[1]) with
| true, port when port > 0 && port < 65536 -> 
    FIOApp.Run(FIOChatServerApp(IPAddress.Loopback, port))
    exit 0
| _ ->
    eprintfn $"Invalid port number: %s{args.[0]}"
    exit 1