using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using UnityEngine.UI;

public class NetworkedServer : MonoBehaviour
{
    private static string PlayerNamesFile = "PlayerAccounts.txt";
    int maxConnections = 1000;
    int reliableChannelID;
    int unreliableChannelID;
    int hostID;
    int socketPort = 5491;
    LinkedList<PlayerAccount> playerAccounts;
    int playerWaitingForMatch = -1;
    LinkedList<GameSession> gameSessions;

    // Start is called before the first frame update
    void Start()
    {
        NetworkTransport.Init();
        ConnectionConfig config = new ConnectionConfig();
        reliableChannelID = config.AddChannel(QosType.Reliable);
        unreliableChannelID = config.AddChannel(QosType.Unreliable);
        HostTopology topology = new HostTopology(config, maxConnections);
        hostID = NetworkTransport.AddHost(topology, socketPort, null);

        playerAccounts = new LinkedList<PlayerAccount>();
        gameSessions = new LinkedList<GameSession>();

        LoadPlayerAccounts();
    }

    // Update is called once per frame
    void Update()
    {

        int recHostID;
        int recConnectionID;
        int recChannelID;
        byte[] recBuffer = new byte[1024];
        int bufferSize = 1024;
        int dataSize;
        byte error = 0;

        NetworkEventType recNetworkEvent = NetworkTransport.Receive(out recHostID, out recConnectionID, out recChannelID, recBuffer, bufferSize, out dataSize, out error);

        switch (recNetworkEvent)
        {
            case NetworkEventType.Nothing:
                break;
            case NetworkEventType.ConnectEvent:
                Debug.Log("Connection, " + recConnectionID);
                break;
            case NetworkEventType.DataEvent:
                string msg = Encoding.Unicode.GetString(recBuffer, 0, dataSize);
                ProcessRecievedMsg(msg, recConnectionID);
                break;
            case NetworkEventType.DisconnectEvent:
                Debug.Log("Disconnection, " + recConnectionID);
                break;
        }

    }
  
    public void SendMessageToClient(string msg, int id)
    {
        byte error = 0;
        byte[] buffer = Encoding.Unicode.GetBytes(msg);
        NetworkTransport.Send(hostID, id, reliableChannelID, buffer, msg.Length * sizeof(char), out error);
    }
    
    private void ProcessRecievedMsg(string msg, int id)
    {
        Debug.Log("msg recieved = " + msg + ".  connection id = " + id);

        string[] csv = msg.Split(',');
        int signifier = int.Parse(csv[0]);

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            string username = csv[1];
            string password = csv[2];

            // Check for existing account
            bool isUnique = true;
            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == username)
                {
                    isUnique = false;
                    break;
                }
            }

            // Add a new account if its unique
            if (isUnique)
            {
                playerAccounts.AddLast(new PlayerAccount(username, password));
                SendMessageToClient(ServerToClientSignifiers.LoginResponse + "," + LoginResponses.Success, id);
                SavePlayerAccounts();
            }
            else
            {
                SendMessageToClient(ServerToClientSignifiers.LoginResponse + "," + LoginResponses.FailureNameInUse, id);
            }
        }
        else if (signifier == ClientToServerSignifiers.Login)
        {
            string username = csv[1];
            string password = csv[2];

            bool isFound = false;
            foreach (PlayerAccount pa in playerAccounts)
            {
                if (pa.name == username)
                {
                    if (pa.password == password)
                    {
                        SendMessageToClient(ServerToClientSignifiers.LoginResponse + "," + LoginResponses.Success, id);
                        pa.currentConnectionID = id;
                    }
                    else
                    {
                        SendMessageToClient(ServerToClientSignifiers.LoginResponse + "," + LoginResponses.FailureIncorrectPassword, id);
                    }
                    isFound = true;

                    break;
                }
            }

            if (!isFound)
            {
                SendMessageToClient(ServerToClientSignifiers.LoginResponse + "," + LoginResponses.FailureNameNotFound, id);
            }
        }
        else if (signifier == ClientToServerSignifiers.AddToGameSessionQueue)
        {
            if (playerWaitingForMatch == -1)
            {
                // represent the only possible waiting player
                playerWaitingForMatch = id;
            }
            else
            {
                // There is a player waiting
                // Start a GameSession with both player IDs
                GameSession newGameSession = new GameSession(playerWaitingForMatch, id);
                gameSessions.AddLast(newGameSession);

                // Determine player roles
                int player1Response = SessionStartedResponses.CrossesPlayer;
                int player2Respnse = SessionStartedResponses.CirclesPlayer;
                if (Random.Range(0, 9) > 4)
                {
                    player1Response = SessionStartedResponses.CirclesPlayer;
                    player2Respnse = SessionStartedResponses.CrossesPlayer;
                }

                // Pass a signifier to both clients that they've joined
                SendMessageToClient(ServerToClientSignifiers.GameSessionStarted + "," + player1Response, id);
                SendMessageToClient(ServerToClientSignifiers.GameSessionStarted + "," + player2Respnse, playerWaitingForMatch);
                playerWaitingForMatch = -1;
            }

        }
        else if (signifier == ClientToServerSignifiers.TicTacToePlay)
        {
            Debug.Log("our next action beckons");

            GameSession gs = FindGameSessionWithPlayerID(id);

            // We have the game state on the server, forward it to the other player
            string currentGameState = csv[1];
            gs.currentGameState = currentGameState;

            if (gs.playerID1 == id)
                SendMessageToClient(ServerToClientSignifiers.OpponentTicTacToePlay + "," + currentGameState, gs.playerID2);
            else
                SendMessageToClient(ServerToClientSignifiers.OpponentTicTacToePlay + "," + currentGameState, gs.playerID1);
            
            foreach(int spectatorID in gs.spectators)
            {
                SendMessageToClient(ServerToClientSignifiers.SpectatorUpdate + "," + currentGameState, spectatorID);
            }
        }
        else if (signifier == ClientToServerSignifiers.LeaveSession)
        {
            GameSession gs = FindGameSessionWithPlayerID(id);
            if (gs == null)
            {
                // The connection id is not a player id
                // It might be a spectator id

                gs = FindSessionWithSpectatorID(id);

                if (gs == null)
                {
                    Debug.LogError(id + " not a player or a spectator?!");
                    return;
                }

                // Only remove the spectator ID, do not terminate the session
                gs.spectators.Remove(id);
                return;
            }

            // Notify both players that session is terminated
            SendMessageToClient(ServerToClientSignifiers.SessionTerminated + "", gs.playerID1);
            SendMessageToClient(ServerToClientSignifiers.SessionTerminated + "", gs.playerID2);
            foreach (int spectatorID in gs.spectators)
            {
                SendMessageToClient(ServerToClientSignifiers.SessionTerminated + "", spectatorID);
            }

            gameSessions.Remove(gs);
        }
        else if (signifier == ClientToServerSignifiers.PlayerMessage)
        {
            GameSession gs = FindGameSessionWithPlayerID(id);
            if (gs == null)
            {
                // The connection id is not a player id
                // It might be a spectator id

                gs = FindSessionWithSpectatorID(id);

                if (gs == null)
                {
                    Debug.LogError(id + " not a player or a spectator?!");
                    return;
                }

                // continue with the logic
            }

            string playerMessage = msg.Substring(2, msg.Length - 2);
            if (gs.playerID1 != id)
                SendMessageToClient(ServerToClientSignifiers.PlayerMessage + "," + playerMessage, gs.playerID1);
            if (gs.playerID2 != id)
                SendMessageToClient(ServerToClientSignifiers.PlayerMessage + "," + playerMessage, gs.playerID2);

            foreach (int spectateorId in gs.spectators)
            {
                if (spectateorId != id)
                    SendMessageToClient(ServerToClientSignifiers.PlayerMessage + "," + playerMessage, spectateorId);
            }
        }
        else if (signifier == ClientToServerSignifiers.RequestSessionIDs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ServerToClientSignifiers.SessionIDResponse);
            sb.Append(",");
            foreach (GameSession gs in gameSessions)
            {
                sb.Append(gs.sessionID);
                sb.Append(",");
            }
            SendMessageToClient(sb.ToString(), id);
        }
        else if (signifier == ClientToServerSignifiers.SpectateSession)
        {
            int sessionId = int.Parse(csv[1]);
            GameSession gs = FindGameSessionWithID(sessionId);
            if (gs == null)
            {
                Debug.LogWarning("Unable to spectate session " + csv[1]);
                return;
            }
            gs.spectators.AddLast(id);
            SendMessageToClient(ServerToClientSignifiers.SpectateStarted + "," + gs.currentGameState, id);
        }
    }

    private void SavePlayerAccounts()
    {       
        StreamWriter sw = new StreamWriter(Application.dataPath + Path.DirectorySeparatorChar + PlayerNamesFile);

        foreach(PlayerAccount pa in playerAccounts)
        {
            sw.WriteLine(pa.name + "," + pa.password);
        }
        sw.Close();
    }

    private void LoadPlayerAccounts()
    {
        string path = Application.dataPath + Path.DirectorySeparatorChar + PlayerNamesFile;
        if (File.Exists(path))
        {
            StreamReader sr = new StreamReader(path);
            string line = "";
            while ((line = sr.ReadLine()) != null)
            {
                string[] csv = line.Split(',');
                playerAccounts.AddLast(new PlayerAccount(csv[0], csv[1]));
            }
            sr.Close();
        }
    }

    private GameSession FindGameSessionWithPlayerID(int id)
    {
        foreach (GameSession gs in gameSessions)
        {
            if (gs.playerID1 == id || gs.playerID2 == id)
            {
                return gs;
            }
        }

        return null;
    }

    private GameSession FindGameSessionWithID(int id)
    {
        foreach (GameSession gs in gameSessions)
        {
            if (gs.sessionID == id)
            {
                return gs;
            }
        }

        return null;
    }

    private GameSession FindSessionWithSpectatorID(int id)
    {
        foreach (GameSession gs in gameSessions)
        {
            foreach (int spectatorID in gs.spectators)
            {
                if (spectatorID == id) return gs;
            }
        }
        return null;
    }

    private PlayerAccount FindPlayerByConnectionID(int connectionID)
    {
        foreach (PlayerAccount pa in playerAccounts)
        {
            if (pa.currentConnectionID == connectionID)
            {
                return pa;
            }
        }

        return null;
    }
}
public static class ClientToServerSignifiers
{
    public const int Login = 1;
    public const int CreateAccount = 2;
    public const int AddToGameSessionQueue = 3;
    public const int TicTacToePlay = 4;
    public const int LeaveSession = 5;
    public const int PlayerMessage = 6;
    public const int RequestSessionIDs = 7;
    public const int SpectateSession = 8;
}

public static class ServerToClientSignifiers
{
    public const int LoginResponse = 1;
    public const int GameSessionStarted = 2;
    public const int OpponentTicTacToePlay = 3;
    public const int SessionTerminated = 4;
    public const int PlayerMessage = 5;
    public const int SessionIDResponse = 6;
    public const int SpectateStarted = 7;
    public const int SpectatorUpdate = 8;
}

public static class SessionStartedResponses
{
    public const int CirclesPlayer = 1;
    public const int CrossesPlayer = 2;
}

public static class LoginResponses
{
    public const int Success = 1;
    public const int FailureNameInUse = 2;
    public const int FailureNameNotFound = 3;
    public const int FailureIncorrectPassword = 4;
}

public class PlayerAccount
{
    public string name;
    public string password;
    public int currentConnectionID = -1;

    public PlayerAccount(string Name, string Password)
    {
        name = Name;
        password = Password;
    }
}

public class GameSession
{
    public int playerID1, playerID2;
    public LinkedList<int> spectators;
    public string currentGameState = "000000000";
    public int sessionID;
    private static int SessionCounter = 1;

    public GameSession(int PlayerID1, int PlayerID2)
    {
        playerID1 = PlayerID1;
        playerID2 = PlayerID2;
        spectators = new LinkedList<int>();
        sessionID = SessionCounter;
        SessionCounter++;
    }
}