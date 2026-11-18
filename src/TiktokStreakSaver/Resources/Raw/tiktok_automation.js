(function () {
    var message = '[Message]';
    var maxChats = parseInt('[MaxChats]' || '10');
    var processedCount = 0;

    var log = function (msg) {
        if (typeof StreakApp == 'undefined') {
            console.log(msg);
            return;
        }
        StreakApp.log(msg);
    };

    var findMessageInput = function () {
        return document.querySelector('div[contenteditable="true"][data-placeholder*="Message"]') ||
            document.querySelector('div[contenteditable="true"][role="textbox"]') ||
            document.querySelector('div.public-DraftEditor-content[contenteditable="true"]') ||
            document.querySelector('div[contenteditable="true"]');
    };

    var typeMessage = function (messageInput, callback) {
        messageInput.click();
        messageInput.focus();

        setTimeout(function () {
            var selection = window.getSelection();
            var range = document.createRange();
            range.selectNodeContents(messageInput);
            range.collapse(false);
            selection.removeAllRanges();
            selection.addRange(range);

            document.execCommand('insertText', false, message);
            setTimeout(callback, 400);
        }, 300);
    };

    var sendMessage = function (messageInput) {
        var sendBtn = document.querySelector('button[type="submit"]') ||
            document.querySelector('[data-e2e*="send"]') ||
            document.querySelector('svg[fill*="currentColor"]')?.closest('button');

        if (sendBtn) {
            sendBtn.dispatchEvent(new Event('click', { bubbles: true }));
            return;
        }

        messageInput.dispatchEvent(new KeyboardEvent('keydown', {
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true
        }));
        messageInput.dispatchEvent(new KeyboardEvent('keyup', {
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true
        }));
    };

    var processNextChat = async function () {
        if (processedCount >= maxChats) {
            log('[RESULT] Completed all ' + maxChats + ' chats successfully.');
            if (typeof StreakApp !== 'undefined') {
                StreakApp.onAutomationComplete(true, 'Completed target count of ' + maxChats);
            }
            return;
        }

        var chatItems = document.querySelectorAll('div[class*="DivChatRoomItem"], div[class*="ItemContainer"], [data-e2e*="conversation-item"], [data-e2e*="chat-item"], ul li');

        if (processedCount >= chatItems.length) {
            log('[SCROLL] Scrolling down to load more chats...');
            window.scrollBy(0, 500);
            setTimeout(processNextChat, 2000);
            return;
        }

        var currentChat = chatItems[processedCount];
        if (!currentChat) {
            log('[RESULT] No more chats available to process.');
            if (typeof StreakApp !== 'undefined') {
                StreakApp.onAutomationComplete(true, 'Processed ' + processedCount + ' chats');
            }
            return;
        }

        var clickableTarget = currentChat.querySelector('a, div[role="button"]') || currentChat;
        clickableTarget.click();
        log('[CHAT] Opened chat index: ' + (processedCount + 1));

        setTimeout(function () {
            var messageInput = findMessageInput();
            if (messageInput) {
                typeMessage(messageInput, function () {
                    sendMessage(messageInput);
                    processedCount++;
                    log('[SUCCESS] Message sent to chat ' + processedCount);
                    setTimeout(processNextChat, 2000);
                });
            } else {
                log('[ERROR] Message input not found, skipping...');
                processedCount++;
                setTimeout(processNextChat, 1500);
            }
        }, 2000);
    };

    var init = function () {
        try {
            log('[INIT] Starting top-down chat automation for ' + maxChats + ' chats.');
            setTimeout(processNextChat, 3000);
        } catch (e) {
            log('[ERROR] Init exception: ' + e.message);
        }
    };

    init();
})();
