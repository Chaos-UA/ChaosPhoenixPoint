==== Localization Modding ====
This folder can be used to override the texts in the game.
Simply put in any number of .csv files with localization texts and they will override the ones in the game.
You can override existing languages only. Not all columns / languages need to be present, but language names must match exactly.

First column is mandatory - it contains the key to be localized (e.g. KEY_OPTIONS_OPTIONS_NAME). Each key belongs to a category. The name of the file is used to specify that category.
For example: "KEY_SECTION_PHOENIX_BASES" should be in a file named "Geoscape.csv" to specify the correct category. KEY_OPTIONS_OPTIONS_NAME should in a file named "Options Screen.csv".

Use "export_localization" cheat to export all current localization by categories in this folder.

If the file name starts with "#", no category will be assumed. In that case first column should contain values in the following format: "category/key".
This is useful if you want to have a few overrides from a single file.

Here is an example csv format for uncategorized (starting with '#') file:
Keys,English,Chinese (Simplified),French,German,Italian,Polish,Russian,Spanish,UI_Tester
Geoscape/KEY_SECTION_PHOENIX_BASES,BASES,基地,BASES,BASEN,BASI,BAZY,БАЗЫ,BASES,BASES
Options Screen/KEY_OPTIONS_OPTIONS_NAME,OPTIONS,选项,OPTIONS,OPTIONEN,OPZIONI,OPCJE,ПАРАМЕТРЫ,OPCIONES,ПАРАМЕТРЫ