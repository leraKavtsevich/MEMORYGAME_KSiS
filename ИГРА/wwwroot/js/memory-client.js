let webSocket = null;
let currentRoomCode = null;
let boardSize = 4;
let cards = [];
let isMyTurn = false;
let waitingForFlipReset = false;
let playersList = [];
let reconnectAttempts = 0;
let heartbeatInterval = null;
let myConnectionId = null;
let amIOwner = false;
let gameStartedFlag = false;

// DOM элементы
const lobbyScreen = document.getElementById('lobbyScreen');
const waitingScreen = document.getElementById('waitingScreen');
const gameScreen = document.getElementById('gameScreen');

// Инициализация при загрузке страницы: генерирует или загружает connectionId и подключается к WebSocket
document.addEventListener('DOMContentLoaded', () => {
    myConnectionId = localStorage.getItem('memory_connection_id');
    if (!myConnectionId) {
        myConnectionId = 'player_' + Math.random().toString(36).substring(2, 15);
        localStorage.setItem('memory_connection_id', myConnectionId);
    }
    console.log("Мой ConnectionId:", myConnectionId);

    connectWebSocket();
    setupEventListeners();
});

// Устанавливает WebSocket соединение с сервером и настраивает обработчики событий
function connectWebSocket() {
    const hostname = window.location.hostname;
    const wsUrl = `ws://${hostname}:8081`;
    console.log("Подключение к WebSocket (ручной):", wsUrl);

    webSocket = new WebSocket(wsUrl);

    webSocket.onopen = () => {
        console.log("WebSocket подключен");
        reconnectAttempts = 0;

        // Регистрируем клиента на сервере
        webSocket.send(JSON.stringify({ type: "register", connectionId: myConnectionId }));

        if (heartbeatInterval) clearInterval(heartbeatInterval);
        heartbeatInterval = setInterval(() => {
            if (webSocket?.readyState === WebSocket.OPEN) {
                webSocket.send(JSON.stringify({ type: "ping", connectionId: myConnectionId }));
            }
        }, 30000);
    };

    webSocket.onmessage = (event) => {
        const message = JSON.parse(event.data);
        console.log("Получено:", message);
        handleMessage(message);
    };

    webSocket.onclose = () => {
        console.log("WebSocket отключен");
        if (heartbeatInterval) clearInterval(heartbeatInterval);
        if (reconnectAttempts < 5) {
            reconnectAttempts++;
            setTimeout(connectWebSocket, 2000 * reconnectAttempts);
        }
    };

    webSocket.onerror = (error) => {
        console.error("WebSocket ошибка:", error);
    };
}

// Обрабатывает входящие сообщения от WebSocket сервера
function handleMessage(message) {
    switch (message.type) {
        case "connected":
            console.log("✅ Сервер подтвердил соединение");
            break;

        case "room_created":
            currentRoomCode = message.roomCode;
            boardSize = message.boardSize;
            document.getElementById('displayRoomCode').innerText = message.roomCode;
            document.getElementById('gameRoomCode').innerText = message.roomCode;
            document.getElementById('waitingBoardSize').innerText = message.boardSize;
            document.getElementById('waitingBoardSize2').innerText = message.boardSize;
            document.getElementById('startGameBtn').style.display = 'block';
            showScreen('waitingScreen');
            break;

        case "game_restarted":
            gameStartedFlag = false;
            console.log("Игра перезапущена!");
            document.getElementById('gameOverModal').style.display = 'none';
            waitingForFlipReset = false;
            isMyTurn = false;
            sendCommand("force_update", { roomCode: currentRoomCode });
            showScreen('waitingScreen');
            document.getElementById('startGameBtn').style.display = 'block';
            const restartInfoText = document.getElementById('waitingInfoText');
            restartInfoText.textContent = 'Игра перезапущена! Нажмите "Начать игру" для новой игры';
            restartInfoText.classList.add('ready');
            break;

        case "game_state":
            cards = message.boardData;
            boardSize = message.boardSize;
            isMyTurn = (message.currentPlayerId === myConnectionId);
            waitingForFlipReset = false;
            renderGameBoard();
            updateTurnIndicator();
            updatePlayersList(playersList);
            break;

        case "board_size_info":
            boardSize = message.boardSize;
            document.getElementById('waitingBoardSize').innerText = message.boardSize;
            document.getElementById('waitingBoardSize2').innerText = message.boardSize;
            break;

        case "ready_to_start":
            const readyInfoText = document.getElementById('waitingInfoText');
            readyInfoText.textContent = '✅ Игроки собраны! Нажмите "Начать игру"';
            readyInfoText.classList.add('ready');
            break;

        case "game_started":
            console.log("Игра началась! firstPlayerId:", message.firstPlayerId);
            cards = message.boardData;
            boardSize = message.boardSize;
            isMyTurn = (message.firstPlayerId === myConnectionId);
            waitingForFlipReset = false;
            renderGameBoard();
            updateTurnIndicator();
            showScreen('gameScreen');
            showTemporaryMessage(isMyTurn ? "Игра началась! Ваш ход!" : "Игра началась! Ход соперника...", 'info');
            break;

        case "card_flipped":
            if (cards[message.cardIndex]) {
                cards[message.cardIndex].isFlipped = true;
                if (message.imagePath) cards[message.cardIndex].imagePath = message.imagePath;
                renderGameBoard();
            }
            break;

        case "players_updated":
            console.log("Список игроков:", message.players);
            playersList = message.players;
            const me = playersList.find(p => p.connectionId === myConnectionId);
            amIOwner = me ? me.isOwner : false;
            updatePlayersList(playersList);
            updateOwnerButtons();
            break;

        case "match_found":
            if (cards[message.firstCardIndex]) cards[message.firstCardIndex].isMatched = true;
            if (cards[message.secondCardIndex]) cards[message.secondCardIndex].isMatched = true;
            renderGameBoard();
            showTemporaryMessage(`${message.playerName} нашёл пару!`, 'success');
            break;

        case "no_match":
            waitingForFlipReset = true;
            setTimeout(() => {
                if (cards[message.firstCardIndex]) cards[message.firstCardIndex].isFlipped = false;
                if (cards[message.secondCardIndex]) cards[message.secondCardIndex].isFlipped = false;
                renderGameBoard();
                if (isMyTurn && currentRoomCode) {
                    sendCommand("next_turn", { roomCode: currentRoomCode });
                } else {
                    waitingForFlipReset = false;
                }
            }, 1200);
            break;

        case "turn_changed":
            const oldIsMyTurn = isMyTurn;
            isMyTurn = (message.nextPlayerId === myConnectionId);
            if (isMyTurn) {
                waitingForFlipReset = false;
                if (!oldIsMyTurn) showTemporaryMessage("Ваш ход!", 'success');
            } else {
                waitingForFlipReset = true;
            }
            updateTurnIndicator();
            renderGameBoard();
            break;

        case "game_over":
            gameStartedFlag = false;
            const modalText = document.getElementById('winnerMessage');
            if (message.winnerId === "DRAW") {
                modalText.innerText = "Ничья!";
            } else if (message.winnerId === myConnectionId) {
                modalText.innerText = "Поздравляем! Вы победили!";
            } else {
                const winner = playersList.find(p => p.connectionId === message.winnerId);
                modalText.innerText = `Победил: ${winner ? winner.name : "Оппонент"}`;
            }
            document.getElementById('gameOverModal').style.display = 'flex';
            break;

        case "player_left":
            showTemporaryMessage(`${message.playerName} покинул игру`, 'warning');
            break;

        case "error":
            alert(message.message);
            if (message.message.includes("не найдена")) location.reload();
            break;
    }
}

// Отправляет команду на сервер через WebSocket
function sendCommand(type, data) {
    if (webSocket?.readyState !== WebSocket.OPEN) {
        console.error("WebSocket не подключен");
        return;
    }
    const command = { type, connectionId: myConnectionId, ...data };
    console.log("Отправка:", command);
    webSocket.send(JSON.stringify(command));
}

// Настраивает обработчики событий для кнопок интерфейса
function setupEventListeners() {
    document.getElementById('createRoomBtn').addEventListener('click', createRoom);
    document.getElementById('playAgainBtn').addEventListener('click', playAgain);
    document.getElementById('joinRoomBtn').addEventListener('click', joinRoom);
    document.getElementById('startGameBtn').addEventListener('click', startGame);
    document.getElementById('leaveWaitingBtn').addEventListener('click', () => location.reload());
    document.getElementById('leaveGameBtn').addEventListener('click', () => location.reload());
    document.getElementById('closeModalBtn').addEventListener('click', () => {
        document.getElementById('gameOverModal').style.display = 'none';
        location.reload();
    });
}

// Создает новую игровую комнату
function createRoom() {
    const playerName = document.getElementById('createPlayerName').value.trim();
    const boardSizeValue = parseInt(document.getElementById('boardSize').value);
    if (!playerName) { alert('Введите имя'); return; }
    if (playerName.length < 2) { alert('Имя минимум 2 символа'); return; }
    sendCommand("create_room", { playerName: playerName, boardSize: boardSizeValue });
}

// Запрашивает перезапуск игры (только для владельца комнаты)
function playAgain() {
    if (currentRoomCode) {
        document.getElementById('gameOverModal').style.display = 'none';
        sendCommand("restart_game", { roomCode: currentRoomCode });
        showTemporaryMessage("Перезапуск игры...", 'info');
    }
}

// Присоединяется к существующей комнате по коду
function joinRoom() {
    const playerName = document.getElementById('joinPlayerName').value.trim();
    const roomCode = document.getElementById('roomCode').value.trim().toUpperCase();
    if (!playerName) { alert('Введите имя'); return; }
    if (!roomCode) { alert('Введите код комнаты'); return; }
    if (roomCode.length !== 4) { alert('Код из 4 символов'); return; }
    currentRoomCode = roomCode;
    document.getElementById('displayRoomCode').innerText = roomCode;
    document.getElementById('gameRoomCode').innerText = roomCode;
    document.getElementById('startGameBtn').style.display = 'none';
    showScreen('waitingScreen');
    sendCommand("join_room", { roomCode: roomCode, playerName: playerName });
}

// Запускает игру (только для владельца комнаты)
function startGame() {
    if (currentRoomCode) {
        document.getElementById('startGameBtn').style.display = 'none';
        sendCommand("start_game", { roomCode: currentRoomCode });
    }
}

// Переворачивает карточку на игровом поле
function flipCard(cardIndex) {
    if (!isMyTurn) { showTemporaryMessage("Не ваш ход!", 'warning'); return; }
    if (waitingForFlipReset) { showTemporaryMessage("Подождите...", 'info'); return; }
    const card = cards[cardIndex];
    if (!card || card.isFlipped || card.isMatched) return;
    sendCommand("flip_card", { roomCode: currentRoomCode, cardIndex: cardIndex });
}

// Обновляет отображение списка игроков в интерфейсе
function updatePlayersList(players) {
    console.log("=== updatePlayersList вызван ===");
    console.log("Данные игроков:", JSON.stringify(players));

    const waitingList = document.getElementById('waitingPlayersList');
    const scoresContainer = document.getElementById('playersScores');

    console.log("waitingList элемент:", waitingList);
    console.log("scoresContainer элемент:", scoresContainer);

    // Обновляем экран ожидания
    if (waitingList) {
        waitingList.innerHTML = '';
        for (let i = 0; i < players.length; i++) {
            const p = players[i];
            const li = document.createElement('li');
            li.textContent = p.name + (p.isCurrent ? ' 🎯' : '') + (p.connectionId === myConnectionId ? ' (Вы)' : '');
            waitingList.appendChild(li);
            console.log("Создан LI с текстом:", li.textContent);
        }
    }

    // Обновляем игровой экран
    if (scoresContainer) {
        scoresContainer.innerHTML = '';
        for (let i = 0; i < players.length; i++) {
            const p = players[i];
            const div = document.createElement('div');
            div.className = 'player-score-card';
            if (p.isCurrent) div.classList.add('current-turn');

            const h4 = document.createElement('h4');
            h4.textContent = p.name + (p.connectionId === myConnectionId ? ' (Вы)' : '');

            const scoreDiv = document.createElement('div');
            scoreDiv.className = 'score';
            scoreDiv.textContent = p.score !== undefined ? p.score : 0;

            div.appendChild(h4);
            div.appendChild(scoreDiv);
            scoresContainer.appendChild(div);
            console.log("Создана карточка:", h4.textContent, "счёт:", scoreDiv.textContent);
        }
    }
}

// Отрисовывает игровое поле на основе текущего состояния карт
function renderGameBoard() {
    const board = document.getElementById('gameBoard');
    if (!board) return;
    board.setAttribute('data-size', boardSize);
    board.innerHTML = '';
    if (!cards || cards.length === 0) return;

    cards.forEach((card, index) => {
        const cardElement = document.createElement('div');
        cardElement.className = 'card';
        if (card.isMatched) {
            cardElement.classList.add('matched');
        } else if (card.isFlipped) {
            cardElement.classList.add('flipped');
            if (card.imagePath) {
                const img = document.createElement('img');
                img.src = card.imagePath;
                img.onerror = () => { cardElement.textContent = '?'; };
                cardElement.appendChild(img);
            } else {
                cardElement.textContent = '?';
            }
        } else {
            const backDiv = document.createElement('div');
            backDiv.style.cssText = 'background: linear-gradient(145deg, #667eea, #764ba2); width:100%; height:100%; border-radius:12px; display:flex; align-items:center; justify-content:center; font-size:1.5em; color:white;';
            backDiv.textContent = '?';
            cardElement.appendChild(backDiv);
        }
        if (!card.isMatched && !card.isFlipped && isMyTurn && !waitingForFlipReset) {
            cardElement.style.cursor = 'pointer';
            cardElement.onclick = (e) => { e.stopPropagation(); flipCard(index); };
        }
        board.appendChild(cardElement);
    });
}

// Обновляет индикатор текущего хода
function updateTurnIndicator() {
    const turnText = document.getElementById('turnText');
    const turnIndicator = document.getElementById('turnIndicator');
    if (isMyTurn) {
        turnText.textContent = '🔥 ВАШ ХОД!';
        turnIndicator.classList.add('your-turn');
    } else {
        turnText.textContent = '⏳ Ход соперника...';
        turnIndicator.classList.remove('your-turn');
    }
}

// Переключает видимость экранов (лобби/ожидание/игра)
function showScreen(screenId) {
    lobbyScreen.classList.remove('active');
    waitingScreen.classList.remove('active');
    gameScreen.classList.remove('active');
    document.getElementById(screenId).classList.add('active');
}

// Показывает временное всплывающее уведомление
function showTemporaryMessage(message, type) {
    let bgColor = type === 'success' ? '#48bb78' : (type === 'warning' ? '#ed8936' : '#667eea');

    const notification = document.createElement('div');
    notification.textContent = message;
    notification.style.cssText = `
        position: fixed;
        bottom: 30px;
        right: 30px;
        padding: 12px 24px;
        background: ${bgColor};
        color: white;
        border-radius: 12px;
        z-index: 2000;
        font-weight: bold;
        box-shadow: 0 4px 15px rgba(0,0,0,0.2);
    `;

    document.body.appendChild(notification);

    setTimeout(() => {
        notification.remove();
    }, 2500);
}

// Обновляет видимость кнопок владельца комнаты
function updateOwnerButtons() {
    const startGameBtn = document.getElementById('startGameBtn');
    const playAgainBtn = document.getElementById('playAgainBtn');

    if (startGameBtn) {
        startGameBtn.style.display = (amIOwner && currentRoomCode && !gameStartedFlag) ? 'block' : 'none';
    }

    if (playAgainBtn) {
        playAgainBtn.style.display = amIOwner ? 'inline-block' : 'none';
    }
}

// Экранирует HTML специальные символы для безопасности
function escapeHtml(str) {
    if (!str) return '';
    return str.replace(/[&<>]/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[m]));
}