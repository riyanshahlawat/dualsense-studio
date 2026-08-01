# Publishing on GitHub

## Create the repository

1. Create a repository named `DualSense-Bluetooth-Audio`.
2. Choose public or private visibility.
3. Do not initialize it with a README, `.gitignore`, or license because those
   files are already included in this folder.

## Push with Git

Open a terminal in the `DualSense-Bluetooth-Audio` folder and run:

```powershell
git init
git add .
git commit -m "Release v0.0.1"
git branch -M main
git remote add origin https://github.com/YOUR_USERNAME/DualSense-Bluetooth-Audio.git
git push -u origin main
```

Replace `YOUR_USERNAME` with your GitHub username.

## Create the first Release

1. Run `BUILD_APP.bat` to create the x64 and x86 packages.
2. On GitHub, open **Releases > Draft a new release**.
3. Create tag `v0.0.1` from the `main` branch.
4. Use `DualSense Bluetooth Audio v0.0.1` as the title.
5. Attach both ZIP files from the `release` folder.
6. Use `CHANGELOG.md` as the basis for the release notes.

## Suggested topics

`dualsense`, `bluetooth-audio`, `windows`, `wpf`, `dotnet`, `hid`, `opus`

After the first push, `.github/workflows/build.yml` also builds the x64 and x86
versions automatically with GitHub Actions.
