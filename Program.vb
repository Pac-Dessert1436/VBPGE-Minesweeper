Option Strict On
Option Infer On
Imports YamlDotNet.Serialization
Imports YamlDotNet.Serialization.NodeTypeResolvers
Imports NAudio.Wave
Imports VbPixelGameEngine

Public NotInheritable Class Program
    Inherits PixelGameEngine

    ' YAML deserializer for loading game patterns
    ' (Note: Type conversions in YAML are unused for the game patterns.)
    Private ReadOnly deserializer As IDeserializer = New DeserializerBuilder() _
        .WithoutNodeTypeResolver(Of TagNodeTypeResolver).Build()

    ' Game states
    Private Enum GameState As Integer
        Playing = 0
        Win = 1
        Lose = 2
    End Enum

    ' Cell states
    Private Enum CellState As Integer
        Hidden = 0
        Revealed = 1
        Flagged = 2
    End Enum

    ' Game elements
    Private Const CELL_SIZE As Integer = 30
    Private Const GRID_OFFSET_X As Integer = 30
    Private Const GRID_OFFSET_Y As Integer = 50
    Private Const GRID_WIDTH As Integer = 25
    Private Const GRID_HEIGHT As Integer = 15
    Private Const MINE_COUNT As Integer = 54
    Private ReadOnly Property ColorTable As New List(Of Pixel) From {
        Presets.Black,     ' 0 - Empty
        Presets.Blue,      ' 1
        Presets.Green,     ' 2
        Presets.Red,       ' 3
        Presets.DarkBlue,  ' 4
        Presets.DarkRed,   ' 5
        Presets.DarkCyan,  ' 6
        Presets.Purple,    ' 7
        Presets.Navy,      ' 8
        Presets.Maroon,    ' 9 - Mine background
        Presets.Mint,      ' 10 - Flag background
        Presets.White,     ' 11 - Hidden cell
        Presets.DarkGrey   ' 12 - Revealed cell
    }

    ' Game data
    Private ReadOnly grid(GRID_WIDTH - 1, GRID_HEIGHT - 1) As Integer
    ' -1 = mine, 0-8 = adjacent mines
    Private ReadOnly cellStates(GRID_WIDTH - 1, GRID_HEIGHT - 1) As CellState
    Private minePattern As Integer(,), flagPattern As Integer(,)
    Private wrongMarkPattern As Integer(,)
    Private currGameState As GameState = GameState.Playing
    Private firstClick As Boolean = True
    Private flagsPlaced As Integer = 0
    Private timeTaken As Single = 0F

    Public Class SoundPlayer
        Private ReadOnly reader As AudioFileReader
        Private ReadOnly waveOut As WaveOutEvent
        Private isLooping As Boolean = False

        Public Sub New(filename As String)
            reader = New AudioFileReader(filename)
            waveOut = New WaveOutEvent
            waveOut.Init(reader)

            AddHandler waveOut.PlaybackStopped, AddressOf OnPlaybackStopped
        End Sub

        Public Sub Play()
            If waveOut IsNot Nothing Then
                isLooping = False
                reader.Position = 0
                waveOut.Play()
            End If
        End Sub

        Public Sub PlayLooping()
            If waveOut IsNot Nothing Then
                isLooping = True
                reader.Position = 0
                waveOut.Play()
            End If
        End Sub

        Public Sub [Stop]()
            If waveOut IsNot Nothing Then
                isLooping = False
                waveOut.Stop()
            End If
        End Sub

        Public Sub OnPlaybackStopped(sender As Object, e As StoppedEventArgs)
            If isLooping AndAlso waveOut IsNot Nothing Then
                reader.Position = 0
                waveOut.Play()
            End If
        End Sub
    End Class

    Private ReadOnly bgmMainTheme As New SoundPlayer("Assets/main_theme.mp3")
    Private ReadOnly sfxGameOver As New SoundPlayer("Assets/game_over.mp3")
    Private ReadOnly sfxVictory As New SoundPlayer("Assets/victory.mp3")

    Public Sub New()
        AppName = "VBPGE Minesweeper"
    End Sub

    Public Shared Function ConvertTo2DArray(pattern As Integer()()) As Integer(,)
        Dim result(10, 10) As Integer
        For i As Integer = 0 To 10
            For j As Integer = 0 To 10
                result(i, j) = pattern(i)(j)
            Next j
        Next i
        Return result
    End Function

    Public Sub DrawPattern(pos As Vi2d, pattern As Integer(,), colors As List(Of Pixel))
        For i As Integer = 0 To 10
            For j As Integer = 0 To 10
                ' Only draw non-transparent pixels
                If pattern(i, j) > 0 Then
                    Draw(New Vi2d(pos.x + j, pos.y + i), colors(pattern(i, j)))
                End If
            Next j
        Next i
    End Sub

    Private Sub InitializeGame()
        ' Reset game state
        bgmMainTheme.Stop()
        sfxGameOver.Stop()
        sfxVictory.Stop()
        Array.Clear(grid, 0, grid.Length)
        For i As Integer = 0 To GRID_WIDTH - 1
            For j As Integer = 0 To GRID_HEIGHT - 1
                cellStates(i, j) = CellState.Hidden
            Next j
        Next i
        firstClick = True
        flagsPlaced = 0
        currGameState = GameState.Playing
        timeTaken = 0
    End Sub

    Private Sub PlaceMines(avoidX As Integer, avoidY As Integer)
        Dim rnd As New Random
        Dim minesPlaced As Integer = 0

        While minesPlaced < MINE_COUNT
            Dim x = rnd.Next(GRID_WIDTH), y = rnd.Next(GRID_HEIGHT)

            ' Don't place mine on first click or where mines already exist
            If (x <> avoidX OrElse y <> avoidY) AndAlso grid(x, y) >= 0 Then
                grid(x, y) = -1 ' -1 represents a mine
                minesPlaced += 1
            End If
        End While

        ' Calculate adjacent mine counts
        For x As Integer = 0 To GRID_WIDTH - 1
            For y As Integer = 0 To GRID_HEIGHT - 1
                If grid(x, y) <> -1 Then grid(x, y) = CountAdjacentMines(x, y)
            Next y
        Next x
    End Sub

    Private Function CountAdjacentMines(x As Integer, y As Integer) As Integer
        Dim count As Integer = 0

        For i As Integer = -1 To 1
            For j As Integer = -1 To 1
                If i = 0 AndAlso j = 0 Then Continue For ' Skip current cell

                Dim nx As Integer = x + i, ny As Integer = y + j

                If nx >= 0 AndAlso nx < GRID_WIDTH AndAlso ny >= 0 AndAlso ny < GRID_HEIGHT _
                    AndAlso grid(nx, ny) = -1 Then count += 1
            Next j
        Next i

        Return count
    End Function

    Private Sub RevealCell(x As Integer, y As Integer)
        If x < 0 OrElse x >= GRID_WIDTH OrElse y < 0 OrElse y >= GRID_HEIGHT Then Exit Sub
        If cellStates(x, y) <> CellState.Hidden Then Exit Sub

        cellStates(x, y) = CellState.Revealed

        Select Case grid(x, y)
            Case -1
                sfxGameOver.Play()
                currGameState = GameState.Lose
            Case 0
                ' Reveal adjacent cells for empty cells
                For i As Integer = -1 To 1
                    For j As Integer = -1 To 1
                        RevealCell(x + i, y + j)
                    Next j
                Next i
        End Select
    End Sub

    Private Sub ToggleFlag(x As Integer, y As Integer)
        If x < 0 OrElse x >= GRID_WIDTH OrElse y < 0 OrElse y >= GRID_HEIGHT OrElse
            cellStates(x, y) = CellState.Revealed Then Exit Sub

        cellStates(x, y) =
            If(cellStates(x, y) = CellState.Hidden, CellState.Flagged, CellState.Hidden)
    End Sub

    Private ReadOnly Property IsGameWon As Boolean
        Get
            Dim HasHiddenCells =
                Function(x As Integer, y As Integer)
                    Return grid(x, y) <> -1 AndAlso cellStates(x, y) <> CellState.Revealed
                End Function

            For x As Integer = 0 To GRID_WIDTH - 1
                For y As Integer = 0 To GRID_HEIGHT - 1
                    If HasHiddenCells(x, y) Then Return False
                Next y
            Next x
            Return True
        End Get
    End Property

    Protected Overrides Function OnUserCreate() As Boolean
        ' Load patterns from YAML
        Dim patterns = deserializer.Deserialize(Of Dictionary(Of String, Integer()()))(
            IO.File.ReadAllText("Assets/Patterns.yml"))

        minePattern = ConvertTo2DArray(patterns("Mine"))
        flagPattern = ConvertTo2DArray(patterns("Flag"))
        wrongMarkPattern = ConvertTo2DArray(patterns("WrongMark"))

        InitializeGame()
        Return True
    End Function

    Protected Overrides Function OnUserUpdate(elapsedTime As Single) As Boolean
        ' Clear screen
        Clear(Presets.Black)

        If Not firstClick AndAlso currGameState = GameState.Playing Then
            timeTaken = Math.Clamp(timeTaken + elapsedTime, 0, 999)
        End If

        ' Count placed flags
        flagsPlaced = 0
        For x As Integer = 0 To GRID_WIDTH - 1
            For y As Integer = 0 To GRID_HEIGHT - 1
                If cellStates(x, y) = CellState.Flagged Then flagsPlaced += 1
            Next y
        Next x

        ' Convert mouse position to grid coordinates
        Dim gridX As Integer = (GetMouseX - GRID_OFFSET_X) \ CELL_SIZE
        Dim gridY As Integer = (GetMouseY - GRID_OFFSET_Y) \ CELL_SIZE

        Dim isWithinPlayArea As Boolean =
            gridX >= 0 AndAlso gridX < GRID_WIDTH AndAlso gridY >= 0 AndAlso gridY < GRID_HEIGHT

        Select Case currGameState
            Case GameState.Playing
                If GetMouse(0).Released AndAlso isWithinPlayArea Then ' Left click
                    If firstClick Then
                        bgmMainTheme.PlayLooping()
                        PlaceMines(gridX, gridY)
                        firstClick = False
                    End If
                    RevealCell(gridX, gridY)
                    If IsGameWon Then
                        For x As Integer = 0 To GRID_WIDTH - 1
                            For y As Integer = 0 To GRID_HEIGHT - 1
                                If grid(x, y) = -1 Then cellStates(x, y) = CellState.Flagged
                            Next y
                        Next x
                        sfxVictory.Play()
                        currGameState = GameState.Win
                    End If
                ElseIf GetMouse(1).Released AndAlso isWithinPlayArea Then ' Right click
                    ToggleFlag(gridX, gridY)
                End If
            Case GameState.Win, GameState.Lose
                bgmMainTheme.Stop()
                If GetKey(Key.SPACE).Pressed Then InitializeGame()
        End Select

        ' Draw grid
        For x As Integer = 0 To GRID_WIDTH - 1
            For y As Integer = 0 To GRID_HEIGHT - 1
                Dim cellPos As New Vi2d(
                    GRID_OFFSET_X + x * CELL_SIZE, GRID_OFFSET_Y + y * CELL_SIZE
                )
                Dim cellRectSize As New Vi2d(CELL_SIZE - 1, CELL_SIZE - 1)

                ' Determine base color based on cell state
                Dim baseColor As Pixel
                Select Case cellStates(x, y)
                    Case CellState.Hidden
                        baseColor = ColorTable(11) ' White for hidden
                    Case CellState.Flagged
                        baseColor = ColorTable(10) ' Cyan for flag
                    Case CellState.Revealed
                        ' Dark red for mines, dark grey for others
                        baseColor = ColorTable(If(grid(x, y) = -1, 9, 12))
                End Select

                ' Fill cell with base color
                FillRect(cellPos, cellRectSize, baseColor)

                ' Draw cell content
                If cellStates(x, y) = CellState.Revealed Then
                    If grid(x, y) = -1 Then ' Mine
                        ' Mine pattern: 0=transparent, 1=black
                        Dim mineColors As New List(Of Pixel) From {
                            baseColor,    ' Transparent (0) - use base color
                            Presets.Black ' Mine color (1)
                        }
                        DrawPattern(New Vi2d(cellPos.x + CELL_SIZE \ 2 - 5,
                                             cellPos.y + CELL_SIZE \ 2 - 5),
                                    minePattern, mineColors)
                    ElseIf grid(x, y) > 0 Then ' Number
                        DrawString(cellPos + New Vi2d(CELL_SIZE \ 2 - 4, CELL_SIZE \ 2 - 4),
                                   grid(x, y).ToString(), ColorTable(grid(x, y)))
                    End If
                ElseIf cellStates(x, y) = CellState.Flagged Then
                    ' Flag pattern: 0=transparent, 1=black, 2=dark-red
                    Dim flagColors As New List(Of Pixel) From {
                        baseColor,      ' Transparent (0) - use base color
                        Presets.Black,  ' Flag pole (1)
                        Presets.DarkRed ' Flag cloth (2)
                    }
                    Dim isWrongMark = (currGameState = GameState.Lose AndAlso grid(x, y) >= 0)
                    DrawPattern(New Vi2d(cellPos.x + CELL_SIZE \ 2 - 5,
                                         cellPos.y + CELL_SIZE \ 2 - 5),
                                If(isWrongMark, wrongMarkPattern, flagPattern), flagColors)
                End If

                If currGameState = GameState.Lose AndAlso grid(x, y) = -1 AndAlso
                    Not cellStates(x, y) = CellState.Flagged Then
                    ' Mine pattern: 0=transparent, 1=black
                    Dim mineColors As New List(Of Pixel) From {
                        baseColor,    ' Transparent (0) - use base color
                        Presets.Black ' Mine color (1)
                    }
                    DrawPattern(New Vi2d(cellPos.x + CELL_SIZE \ 2 - 5,
                                         cellPos.y + CELL_SIZE \ 2 - 5),
                                minePattern, mineColors)
                End If

                ' Draw grid lines
                DrawRect(cellPos, New Vi2d(CELL_SIZE, CELL_SIZE), Presets.Gray)
            Next y
        Next x

        ' Draw game status
        DrawString(New Vi2d(35, 10), $"Flags: {flagsPlaced} of {MINE_COUNT}", Presets.White, 2)
        DrawString(New Vi2d(450, 10), $"Time Taken: {timeTaken,3:F0} sec.", Presets.White, 2)

        Dim messagePos As New Vi2d(80, ScreenHeight - 55), offset As New Vi2d(0, 15)
        If currGameState = GameState.Win Then
            DrawString(messagePos - offset, "CONGRATULATIONS! Thanks for your patience.", Presets.Green, 2)
            DrawString(messagePos + offset, "Press SPACE to start a new game.", Presets.Green, 2)
        ElseIf currGameState = GameState.Lose Then
            DrawString(messagePos, "GAME OVER! Press SPACE to play again.", Presets.Red, 2)
        End If

        Return Not GetKey(Key.ESCAPE).Pressed
    End Function

    Friend Shared Sub Main()
        With New Program
            If .Construct(800, 600, fullScreen:=True) Then .Start()
        End With
    End Sub
End Class