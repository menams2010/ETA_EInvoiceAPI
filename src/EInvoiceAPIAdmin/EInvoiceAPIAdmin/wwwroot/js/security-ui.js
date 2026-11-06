document.addEventListener('click', function (event) {
    const button = event.target.closest('button[data-confirm]');
    if (button && !window.confirm(button.dataset.confirm)) {
        event.preventDefault();
        event.stopImmediatePropagation();
    }
}, true);
