import pyautogui
import time
import keyboard

# Coordenadas de los dos botones
boton_1 = (1149, 760)
boton_2 = (865, 634)

delay = 0.1  # velocidad entre clics (ajústala si quieres)

print("Autoclicker iniciado. Presiona ESC para detenerlo.")

try:
    while True:
        if keyboard.is_pressed("esc"):
            print("Autoclicker detenido.")
            break

        # Clic en botón 1
        pyautogui.moveTo(boton_1[0], boton_1[1], duration=0.1)
        pyautogui.click()
        time.sleep(delay)

        # Clic en botón 2
        pyautogui.moveTo(boton_2[0], boton_2[1], duration=0.1)
        pyautogui.click()
        time.sleep(delay)

except KeyboardInterrupt:
    print("Programa terminado.")
